using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using Heimdall;
using Microsoft.Data.Sqlite;

namespace Heimdall.Storage.SQLite;

/// <summary>
/// Heimdall-Storage-Backend auf SQLite (Microsoft.Data.Sqlite, FTS5 aktiviert).
/// Implementiert <see cref="IHeimdallSink"/> (Schreiben) und <see cref="IHeimdallQuery"/>
/// (Lesen). Schema: heim_spans / heim_logs / heim_metrics + FTS5-Virtual-Tables
/// (external content) fuer name/body. Parametrisierte Batch-Inserts in einer
/// Transaktion pro Batch; ein Retention-Sweeper loescht alte Zeilen.
///
/// Reif/stabil und mit Standard-Werkzeugen (z. B. DB Browser for SQLite) inspizierbar.
/// Austauschbar mit dem Walhalla-Backend, da beide dieselben Vertrags-Interfaces
/// implementieren.
///
/// Thread-Safety: eine dauerhaft offene Verbindung, alle DB-Zugriffe hinter einem
/// Lock (SQLite serialisiert Schreiber ohnehin). WAL-Mode fuer konkurrente Leser.
/// </summary>
public sealed partial class SQLiteTelemetrySink : IHeimdallSink, IHeimdallQuery, IDisposable
{
    private readonly SQLiteTelemetryOptions _options;
    private readonly SqliteConnection _conn;
    private readonly SqliteCommand _insSpan, _insLog, _insMetric, _insSeries, _selSeriesId;
    private readonly SqliteParameter[] _pSpan, _pLog, _pMetric, _pSeries;
    private readonly Timer? _retentionTimer;
    private readonly object _gate = new();
    private readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    private const int MaxRegexCacheEntries = 256;
    private int _disposed;

    // Retention- & Eviction-Zähler (A4-Observability, siehe SQLiteTelemetrySink.MetricSource.cs).
    private long _retDeletedSpans, _retDeletedLogs, _retDeletedMetrics;
    private long _retEvictedSpans, _retEvictedLogs, _retEvictedMetrics;

    // Host-Self-Observability (Workstream C, C3): Ingest-Volume pro Signal +
    // Latenz des letzten Retention-Sweeps. Synthetisiert (in-memory, nicht in
    // heim_metrics gespeichert) — siehe SQLiteTelemetrySink.MetricSource.cs.
    private long _hostIngestSpans, _hostIngestLogs, _hostIngestMetrics;
    private long _hostSweepDurationTicks;   // Ticks des letzten realen Sweeps (Gauge).

    // Read-Connection-String mit Pooling=True: Reads nutzen gepoolte Verbindungen,
    // die WAL-konkurrent neben dem Writer laufen (vorher: eine Verbindung + globaler
    // _gate-Lock serialisierte alles — WAL wirkte nicht). Writes bleiben hinter
    // _gate + _conn (Serialisierung der Schreiber, wie SQLite es verlangt).
    // Pooling=True erlaubt Microsoft.Data.Sqlite, Verbindungen wiederzuverwenden
    // (ohne pro Query Open/Close-Overhead). Mode=ReadOnly wäre ideal, ist aber
    // nicht mit der REGEXP-Funktion kompatibel (Custom-Functions brauchen RW).
    private readonly string _readConnString;

    public SQLiteTelemetrySink(SQLiteTelemetryOptions? options = null)
    {
        _options = options ?? new SQLiteTelemetryOptions();
        _options.Validate();
        // Write-Verbindung: Pooling=False (Dispose gibt den Datei-Handle frei, wichtig
        // fuer Tests/Cleanup und eingebetteten Betrieb, in dem die Datei verschoben
        // werden soll).
        _conn = new SqliteConnection($"Data Source={_options.DataPath};Pooling=False");
        _conn.Open();
        // Read-Verbindungen: Pooling=True, WAL-konkurrent neben dem Writer.
        // Microsoft.Data.Sqlite unterstützt kein "Max Pool Size" — das Pool wächst
        // nach Bedarf und wird über ClearPool/Dispose bereinigt.
        _readConnString = $"Data Source={_options.DataPath};Pooling=True";
        // Eigene REGEXP-Funktion fuer index-gestuetzte Attribut-Regex-Suche
        // (=~ / !~); Konvention wie SafeRegex (CultureInvariant, 200 ms Timeout,
        // gecacht). SQLite ruft REGEXP(value, pattern) auf — `value REGEXP pat`.
        // SQLite ruft den REGEXP-Operator als REGEXP(pattern, value) auf, d. h. der
        // 1. Funktionsparameter ist das Regex-Pattern, der 2. der zu testende Wert.
        // Registriert auf der Write-Verbindung; Read-Verbindungen registrieren sie
        // beim ersten Öffnen via EnsureReadFunctions (Custom-Functions sind pro
        // Verbindung, nicht pro DB).
        _conn.CreateFunction("REGEXP", (string? pattern, string? value) =>
        {
            if (value is null || string.IsNullOrEmpty(pattern)) return false;
            if (!_regexCache.TryGetValue(pattern, out var r))
            {
                try { r = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
                catch { return false; }   // ungueltiges Pattern -> kein Treffer
                if (_regexCache.Count >= MaxRegexCacheEntries) _regexCache.Clear();
                _regexCache[pattern] = r;
            }
            return r.IsMatch(value);
        });
        if (_options.WalMode)
        {
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=NORMAL;");
        }
        Exec("PRAGMA foreign_keys=ON;");

        // auto_vacuum + Legacy-Migration (A3). auto_vacuum muss VOR der ersten
        // Tabellen-Anlage stehen (wirkt nur auf frische DBs); bestehende Legacy-
        // DBs (auto_vacuum=0, user_version=0) werden einmalig per VACUUM migriert.
        BootstrapAutoVacuum();

        BootstrapSchema();

        // Frische DB (jetzt mit Tabellen) auf user_version=1 heben. Legacy-DBs
        // mit Notaus (nicht migriert) bleiben auf 0, damit sie spaeter noch
        // migriert werden koennen.
        if (_options.AutoVacuum && (int)PragmaLong("auto_vacuum") == 2 && PragmaLong("user_version") == 0)
            Exec("PRAGMA user_version = 1;");

        (_insSpan, _pSpan) = Prepare(SqlInsertSpan, 16);
        (_insLog, _pLog) = Prepare(SqlInsertLog, 10);
        (_insMetric, _pMetric) = Prepare(SqlInsertMetric, 17);
        // Serie (Labels) je Punkt aufloesen: INSERT OR IGNORE + SELECT series_id.
        (_insSeries, _pSeries) = Prepare(SqlInsertSeries, 5);
        _selSeriesId = new SqliteCommand(SqlSelectSeriesId, _conn);
        _selSeriesId.Parameters.Add(new SqliteParameter("@n", null));
        _selSeriesId.Parameters.Add(new SqliteParameter("@a", null));
        _selSeriesId.Parameters.Add(new SqliteParameter("@r", null));
        _selSeriesId.Parameters.Add(new SqliteParameter("@sn", null));
        _selSeriesId.Parameters.Add(new SqliteParameter("@sv", null));
        _selSeriesId.Prepare();

        if (_options.SweepActive)
        {
            _retentionTimer = new Timer(_ => SweepRetention(), null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(_options.RetentionSweepMinutes));
        }
    }

    // -----------------------------------------------------------------------
    // IHeimdallSink
    // -----------------------------------------------------------------------

    public void WriteSpans(IReadOnlyList<HSpan> spans)
    {
        if (spans is null || spans.Count == 0) return;
        lock (_gate)
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1) return;   // C4: kein _conn-Zugriff nach Dispose
            using var tx = _conn.BeginTransaction();
            _insSpan.Transaction = tx;
            for (int i = 0; i < spans.Count; i++)
            {
                var s = spans[i];
                _pSpan[0].Value = HeimdallJson.ToHex(s.TraceId);
                _pSpan[1].Value = HeimdallJson.ToHex(s.SpanId);
                _pSpan[2].Value = s.ParentSpanId is null ? DBNull.Value : (object)HeimdallJson.ToHex(s.ParentSpanId);
                _pSpan[3].Value = s.Name ?? string.Empty;
                _pSpan[4].Value = (int)s.Kind;
                _pSpan[5].Value = s.StartUnixNano;
                _pSpan[6].Value = s.EndUnixNano;
                _pSpan[7].Value = Math.Max(0, s.EndUnixNano - s.StartUnixNano);
                _pSpan[8].Value = (int)s.StatusCode;
                _pSpan[9].Value = (object?)s.StatusMessage ?? DBNull.Value;
                _pSpan[10].Value = HeimdallJson.WriteAttributes(s.Attributes);
                _pSpan[11].Value = HeimdallJson.WriteSpanEvents(s.Events);
                _pSpan[12].Value = HeimdallJson.WriteSpanLinks(s.Links);
                _pSpan[13].Value = s.Resource is null ? "{}" : HeimdallJson.WriteAttributes(s.Resource.Attributes);
                _pSpan[14].Value = (object?)s.Scope?.Name ?? DBNull.Value;
                _pSpan[15].Value = (object?)s.Scope?.Version ?? DBNull.Value;
                _insSpan.ExecuteNonQuery();
            }
            tx.Commit();
            Interlocked.Add(ref _hostIngestSpans, spans.Count);   // C3: ingested-Volume
        }
    }

    public void WriteLogs(IReadOnlyList<HLogRecord> logs)
    {
        if (logs is null || logs.Count == 0) return;
        lock (_gate)
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1) return;   // C4: kein _conn-Zugriff nach Dispose
            using var tx = _conn.BeginTransaction();
            _insLog.Transaction = tx;
            for (int i = 0; i < logs.Count; i++)
            {
                var l = logs[i];
                _pLog[0].Value = l.TimeUnixNano;
                _pLog[1].Value = l.TraceId is null ? DBNull.Value : (object)HeimdallJson.ToHex(l.TraceId);
                _pLog[2].Value = l.SpanId is null ? DBNull.Value : (object)HeimdallJson.ToHex(l.SpanId);
                _pLog[3].Value = (int)l.Severity;
                _pLog[4].Value = (object?)l.SeverityText ?? DBNull.Value;
                _pLog[5].Value = (object?)l.Body ?? DBNull.Value;
                _pLog[6].Value = HeimdallJson.WriteAttributes(l.Attributes);
                _pLog[7].Value = l.Resource is null ? "{}" : HeimdallJson.WriteAttributes(l.Resource.Attributes);
                _pLog[8].Value = (object?)l.Scope?.Name ?? DBNull.Value;
                _pLog[9].Value = (object?)l.Scope?.Version ?? DBNull.Value;
                _insLog.ExecuteNonQuery();
            }
            tx.Commit();
            Interlocked.Add(ref _hostIngestLogs, logs.Count);   // C3: ingested-Volume
        }
    }

    public void WriteMetrics(IReadOnlyList<HMetricPoint> metrics)
    {
        if (metrics is null || metrics.Count == 0) return;
        lock (_gate)
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1) return;   // C4: kein _conn-Zugriff nach Dispose
            using var tx = _conn.BeginTransaction();
            _insMetric.Transaction = tx;
            _insSeries.Transaction = tx;
            _selSeriesId.Transaction = tx;
            for (int i = 0; i < metrics.Count; i++)
            {
                var m = metrics[i];
                // Serie (Labels) einmal je Punkt aufloesen — attrs/resource/scope
                // liegen jetzt in heim_metric_series, nicht mehr pro Punkt dupliziert.
                string attrs = HeimdallJson.WriteAttributes(m.Attributes);
                string resource = m.Resource is null ? "{}" : HeimdallJson.WriteAttributes(m.Resource.Attributes);
                string scopeName = m.Scope?.Name ?? "";
                string scopeVersion = m.Scope?.Version ?? "";
                _pSeries[0].Value = m.Name;
                _pSeries[1].Value = attrs;
                _pSeries[2].Value = resource;
                _pSeries[3].Value = scopeName;
                _pSeries[4].Value = scopeVersion;
                _insSeries.ExecuteNonQuery();
                _selSeriesId.Parameters[0].Value = m.Name;
                _selSeriesId.Parameters[1].Value = attrs;
                _selSeriesId.Parameters[2].Value = resource;
                _selSeriesId.Parameters[3].Value = scopeName;
                _selSeriesId.Parameters[4].Value = scopeVersion;
                long seriesId = (long)_selSeriesId.ExecuteScalar()!;

                _pMetric[0].Value = m.Name;
                _pMetric[1].Value = (object?)m.Unit ?? DBNull.Value;
                _pMetric[2].Value = (int)m.Type;
                _pMetric[3].Value = (int)m.Temporality;
                _pMetric[4].Value = m.TimeUnixNano;
                _pMetric[5].Value = m.Value;
                _pMetric[6].Value = (object?)m.Count ?? DBNull.Value;
                _pMetric[7].Value = (object?)m.Sum ?? DBNull.Value;
                _pMetric[8].Value = (object?)m.Min ?? DBNull.Value;
                _pMetric[9].Value = (object?)m.Max ?? DBNull.Value;
                _pMetric[10].Value = HeimdallJson.WriteLongs(m.BucketCounts);
                _pMetric[11].Value = HeimdallJson.WriteDoubles(m.ExplicitBounds);
                _pMetric[12].Value = DBNull.Value;   // attrs_json — in heim_metric_series
                _pMetric[13].Value = DBNull.Value;   // resource_json — in heim_metric_series
                _pMetric[14].Value = DBNull.Value;   // scope_name — in heim_metric_series
                _pMetric[15].Value = DBNull.Value;   // scope_version — in heim_metric_series
                _pMetric[16].Value = seriesId;
                _insMetric.ExecuteNonQuery();
            }
            tx.Commit();
            Interlocked.Add(ref _hostIngestMetrics, metrics.Count);   // C3: ingested-Volume
        }
    }

    // -----------------------------------------------------------------------
    // IHeimdallQuery
    // -----------------------------------------------------------------------

    public IReadOnlyList<TraceSummary> ListTraces(TraceFilter filter)
    {
        filter ??= new TraceFilter();
        var sb = SqlBuilder();
        sb.Append("SELECT trace_id, MIN(start_unix_nano) AS first_start, MAX(end_unix_nano) AS last_end, ");
        sb.Append("COUNT(*) AS cnt, MAX(CASE WHEN status_code=2 THEN 1 ELSE 0 END) AS err ");
        sb.Append("FROM heim_spans WHERE 1=1");
        var ps = new List<SqliteParameter>();
        AddRange(sb, ps, filter);
        if (filter.ServiceName is not null || filter.ServiceVersion is not null)
        {
            // Service-/Version-Filter index-gestuetzt ueber heim_span_attrs (statt
            // frueherem resource_json LIKE — Full-Scan mit Substring-Semantik).
            // INTERSECT erzwingt Paar-Semantik auf DEMSELBEN Span (Name+Version
            // zusammen), nicht „irgendein Span hat den Namen, irgendein die Version".
            // Semantik-Aenderung: exakter Match statt Substring — alte Bookmarks
            // mit Teil-Strings filtern strenger.
            sb.Append(" AND trace_id IN (SELECT trace_id FROM heim_spans WHERE rowid IN (" +
                      "SELECT span_rowid FROM heim_span_attrs WHERE key IN ('service.name','service_name') AND value = @svc");
            if (filter.ServiceVersion is not null)
                sb.Append(" INTERSECT SELECT span_rowid FROM heim_span_attrs WHERE key IN ('service.version','service_version') AND value = @svcver");
            sb.Append("))");
            if (filter.ServiceName is not null) ps.Add(Param("@svc", filter.ServiceName));
            if (filter.ServiceVersion is not null) ps.Add(Param("@svcver", filter.ServiceVersion));
        }
        if (filter.NameContains is not null)
        {
            sb.Append(" AND trace_id IN (SELECT s2.trace_id FROM heim_spans s2 WHERE s2.rowid IN (SELECT rowid FROM heim_spans_fts WHERE heim_spans_fts MATCH @nm))");
            ps.Add(Param("@nm", SanitizeFts5(filter.NameContains)));
        }
        sb.Append(" GROUP BY trace_id");
        if (filter.HasError is not null)
            sb.Append(filter.HasError.Value ? " HAVING err=1" : " HAVING err=0");
        // Sortierung VOR dem Paging (Allowlist — kein Raw-Input im SQL).
        //   start → first_start (Alias) · duration → (MAX(end)-MIN(start))
        //   spans → cnt · status → err (0/1). Default: first_start DESC.
        string orderExpr = filter.Sort switch
        {
            "duration" => "(MAX(end_unix_nano) - MIN(start_unix_nano))",
            "spans" => "cnt",
            "status" => "err",
            "start" => "first_start",
            _ => "first_start",
        };
        string dir = string.Equals(filter.Dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        sb.Append(" ORDER BY ").Append(orderExpr).Append(' ').Append(dir)
          .Append(" LIMIT @lim OFFSET @off");
        ps.Add(Param("@lim", filter.Limit));
        ps.Add(Param("@off", filter.Offset));

        var list = new List<TraceSummary>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                list.Add(new TraceSummary(
                    r.GetString(0), r.GetInt64(1), r.GetInt64(2),
                    Math.Max(0, r.GetInt64(2) - r.GetInt64(1)),
                    r.GetInt32(3), r.GetInt32(4) == 1));
            }
        }
        return list;
    }

    public IReadOnlyList<SpanRow> GetTrace(string traceId)
    {
        const string sql =
            "SELECT trace_id, span_id, parent_id, name, kind, start_unix_nano, end_unix_nano, " +
            "duration_ns, status_code, status_msg, attrs_json, events_json, resource_json, scope_name " +
            "FROM heim_spans WHERE trace_id=@t ORDER BY start_unix_nano";
        var list = new List<SpanRow>();
        using (var rc = OpenReadConnection())
        {
            using var cmd = new SqliteCommand(sql, rc);
            cmd.Parameters.AddWithValue("@t", traceId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadSpan(r));
        }
        return list;
    }

    public IReadOnlyList<SpanRow> ListSpans(SpanFilter filter)
    {
        filter ??= new SpanFilter();
        var sb = SqlBuilder();
        sb.Append("SELECT trace_id, span_id, parent_id, name, kind, start_unix_nano, end_unix_nano, " +
                  "duration_ns, status_code, status_msg, attrs_json, events_json, resource_json, scope_name " +
                  "FROM heim_spans WHERE 1=1");
        var ps = new List<SqliteParameter>();
        if (filter.FromUnixNano is not null) { sb.Append(" AND start_unix_nano >= @from"); ps.Add(Param("@from", filter.FromUnixNano.Value)); }
        if (filter.ToUnixNano is not null) { sb.Append(" AND start_unix_nano <= @to"); ps.Add(Param("@to", filter.ToUnixNano.Value)); }
        if (filter.Kind is not null) { sb.Append(" AND kind = @kind"); ps.Add(Param("@kind", filter.Kind.Value)); }
        if (filter.MinStatusCode is not null) { sb.Append(" AND status_code >= @min"); ps.Add(Param("@min", filter.MinStatusCode.Value)); }
        sb.Append(" ORDER BY start_unix_nano DESC LIMIT @lim OFFSET @off");
        ps.Add(Param("@lim", filter.Limit < 1 ? 5000 : filter.Limit));
        ps.Add(Param("@off", filter.Offset < 0 ? 0 : filter.Offset));

        var list = new List<SpanRow>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) list.Add(ReadSpan(r));
        }
        return list;
    }

    public IReadOnlyList<LogRow> SearchLogs(LogSearch search)
    {
        search ??= new LogSearch();
        // Dedizierte Service-/Version-Felder (Dropdown der UI) intern auf AttrFilter
        // abbilden -> derselbe index-gestuetzte Pfad, UND-verknuepft mit den
        // LogQL-Feldfiltern aus AttrFilters (Konfliktfall, z. B. q={service.name="a"}
        // + Dropdown "b", liefert leer — vorhersagbar, keine Merge-Magie).
        if (search.ServiceName is not null || search.ServiceVersion is not null)
        {
            var filters = new List<AttrFilter>(search.AttrFilters ?? (IReadOnlyList<AttrFilter>)Array.Empty<AttrFilter>());
            if (search.ServiceName is not null)
                filters.Add(new AttrFilter("service.name", "=", search.ServiceName));
            if (search.ServiceVersion is not null)
                filters.Add(new AttrFilter("service.version", "=", search.ServiceVersion));
            search = search with { AttrFilters = filters };
        }
        var sb = SqlBuilder();
        sb.Append("SELECT ts_unix_nano, trace_id, span_id, severity, severity_text, body, attrs_json, scope_name FROM heim_logs WHERE 1=1");
        var ps = new List<SqliteParameter>();
        if (!string.IsNullOrWhiteSpace(search.Text))
        {
            sb.Append(" AND rowid IN (SELECT rowid FROM heim_logs_fts WHERE heim_logs_fts MATCH @q)");
            ps.Add(Param("@q", SanitizeFts5(search.Text)));
        }
        if (search.MinSeverity is not null) { sb.Append(" AND severity >= @sev"); ps.Add(Param("@sev", search.MinSeverity.Value)); }
        if (search.FromUnixNano is not null) { sb.Append(" AND ts_unix_nano >= @from"); ps.Add(Param("@from", search.FromUnixNano.Value)); }
        if (search.ToUnixNano is not null) { sb.Append(" AND ts_unix_nano <= @to"); ps.Add(Param("@to", search.ToUnixNano.Value)); }
        if (search.TraceId is not null) { sb.Append(" AND trace_id = @tid"); ps.Add(Param("@tid", search.TraceId)); }
        // Attribut-Feldsuche (index-gestuetzt ueber heim_log_attrs; deckt Log- UND
        // Resource-Attribute). Key-Normalisierung: User darf service.name ODER
        // service_name schreiben -> Subquery fragt beide Formen ab.
        if (search.AttrFilters is { Count: > 0 })
        {
            int i = 0;
            foreach (var af in search.AttrFilters)
            {
                string k = af.Key ?? "";
                string kAlt = k.Contains('.') ? k.Replace('.', '_') : k.Replace('_', '.');
                string pKey = "@ak" + i.ToString(CultureInfo.InvariantCulture);
                string pKeyAlt = "@aka" + i.ToString(CultureInfo.InvariantCulture);
                string pVal = "@av" + i.ToString(CultureInfo.InvariantCulture);
                string sub = "SELECT log_rowid FROM heim_log_attrs WHERE key IN (" + pKey + "," + pKeyAlt + ")";
                switch (af.Op)
                {
                    case "=":
                        sb.Append(" AND rowid IN (").Append(sub).Append(" AND value = ").Append(pVal).Append(')');
                        ps.Add(Param(pKey, k)); ps.Add(Param(pKeyAlt, kAlt)); ps.Add(Param(pVal, af.Value ?? ""));
                        break;
                    case "!=":
                        // strict: Log muss das Attr besitzen UND Wert weicht ab (Loki-Semantik).
                        sb.Append(" AND rowid IN (").Append(sub).Append(" AND value <> ").Append(pVal).Append(')');
                        ps.Add(Param(pKey, k)); ps.Add(Param(pKeyAlt, kAlt)); ps.Add(Param(pVal, af.Value ?? ""));
                        break;
                    case "=~":
                        sb.Append(" AND rowid IN (").Append(sub).Append(" AND value REGEXP ").Append(pVal).Append(')');
                        ps.Add(Param(pKey, k)); ps.Add(Param(pKeyAlt, kAlt)); ps.Add(Param(pVal, af.Value ?? ""));
                        break;
                    case "!~":
                        // strict: Log muss das Attr besitzen UND Wert matcht nicht.
                        sb.Append(" AND rowid IN (").Append(sub).Append(" AND NOT value REGEXP ").Append(pVal).Append(')');
                        ps.Add(Param(pKey, k)); ps.Add(Param(pKeyAlt, kAlt)); ps.Add(Param(pVal, af.Value ?? ""));
                        break;
                }
                i++;
            }
        }
        // Sortierung VOR dem Paging (Allowlist — kein Raw-Input im SQL).
        //   time → ts_unix_nano · severity → severity. Default: ts_unix_nano DESC.
        string orderExpr = string.Equals(search.Sort, "severity", StringComparison.OrdinalIgnoreCase)
            ? "severity" : "ts_unix_nano";
        string dir = string.Equals(search.Dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        sb.Append(" ORDER BY ").Append(orderExpr).Append(' ').Append(dir)
          .Append(" LIMIT @lim OFFSET @off");
        ps.Add(Param("@lim", search.Limit));
        ps.Add(Param("@off", search.Offset));

        var list = new List<LogRow>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) list.Add(ReadLog(r));
        }
        return list;
    }

    public IReadOnlyList<MetricRow> MetricSeries(string name, long? fromUnixNano, long? toUnixNano, int limit = 500)
    {
        // Kein Name → keine Serie (z. B. Dashboard ohne Errors-Counter). Früher warf die
        // SQLite-Bindung „Value must be set", weil Param("@n", null) den Parameter ohne
        // Value ließ. Ein leerer Name matcht ohnehin keine Metrik → defensive Früh-Rückkehr.
        if (string.IsNullOrWhiteSpace(name))
            return Array.Empty<MetricRow>();

        var sb = SqlBuilder();
        // Hebel 4: attrs_json aus heim_metric_series (LEFT JOIN + COALESCE-Fallback
        // fuer Legacy-Zeilen ohne series_id).
        sb.Append("SELECT m.name, m.unit, m.type, m.temporality, m.ts_unix_nano, m.value, m.count, m.sum, m.min, m.max, m.bucket_counts_json, m.explicit_bounds_json, COALESCE(s.attrs_json, m.attrs_json) " +
                  "FROM heim_metrics m LEFT JOIN heim_metric_series s ON s.series_id = m.series_id WHERE m.name=@n");
        var ps = new List<SqliteParameter> { Param("@n", name) };
        if (fromUnixNano is not null) { sb.Append(" AND m.ts_unix_nano >= @from"); ps.Add(Param("@from", fromUnixNano.Value)); }
        if (toUnixNano is not null) { sb.Append(" AND m.ts_unix_nano <= @to"); ps.Add(Param("@to", toUnixNano.Value)); }
        sb.Append(" ORDER BY m.ts_unix_nano ASC LIMIT @lim");
        ps.Add(Param("@lim", limit));

        var list = new List<MetricRow>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                list.Add(new MetricRow(
                    r.GetString(0), NStr(r, 1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4),
                    r.GetDouble(5), NLong(r, 6), NDouble(r, 7), NDouble(r, 8), NDouble(r, 9),
                    NStr(r, 10), NStr(r, 11), NStr(r, 12) ?? "{}"));
            }
        }
        return list;
    }

    public long CountSpans() => Count("heim_spans");
    public long CountLogs() => Count("heim_logs");
    public long CountMetrics() => Count("heim_metrics");
    // Workstream F — Rollup-Zeilenzahl (Test-Hook).
    internal long CountMetricsRollup() => Count("heim_metrics_rollup");
    // Hebel 4 — Serien-Zeilenzahl (Test-Hook).
    internal long CountMetricSeries() => Count("heim_metric_series");

    private long Count(string table)
    {
        using var rc = OpenReadConnection();
        using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table}", rc);
        return (long)cmd.ExecuteScalar()!;
    }

    // -----------------------------------------------------------------------
    // Helfer
    // -----------------------------------------------------------------------

    private (SqliteCommand, SqliteParameter[]) Prepare(string sql, int n)
    {
        var cmd = new SqliteCommand(sql, _conn);
        var ps = new SqliteParameter[n];
        for (int i = 0; i < n; i++)
        {
            ps[i] = new SqliteParameter("@p" + i.ToString(CultureInfo.InvariantCulture), null);
            cmd.Parameters.Add(ps[i]);
        }
        cmd.Prepare();
        return (cmd, ps);
    }

    private void AddRange(StringBuilder sb, List<SqliteParameter> ps, TraceFilter f)
    {
        if (f.FromUnixNano is not null) { sb.Append(" AND start_unix_nano >= @from"); ps.Add(Param("@from", f.FromUnixNano.Value)); }
        if (f.ToUnixNano is not null) { sb.Append(" AND start_unix_nano <= @to"); ps.Add(Param("@to", f.ToUnixNano.Value)); }
    }

    private static StringBuilder SqlBuilder() => new StringBuilder(256);

    private SqliteCommand Build(string sql, List<SqliteParameter> ps)
    {
        var cmd = new SqliteCommand(sql, _conn);
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd;
    }

    /// <summary>
    /// Öffnet eine gepoolte Read-Verbindung (WAL-konkurrent neben dem Writer)
    /// und registriert die REGEXP-Funktion (Custom-Functions sind pro Verbindung).
    /// Der Aufrufer nutzt die Verbindung in <paramref name="action"/> und disposet
    /// sie implizit (using). Entkoppelt Reads vom globalen _gate-Lock — Dashboard-
    /// Queries blocken den Ingest nicht mehr und umgekehrt.
    /// </summary>
    private SqliteConnection OpenReadConnection()
    {
        var c = new SqliteConnection(_readConnString);
        c.Open();
        // REGEXP pro Verbindung registrieren (gleiche Logik wie Write-Verbindung).
        c.CreateFunction("REGEXP", (string? pattern, string? value) =>
        {
            if (value is null || string.IsNullOrEmpty(pattern)) return false;
            if (!_regexCache.TryGetValue(pattern, out var r))
            {
                try { r = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
                catch { return false; }
                if (_regexCache.Count >= MaxRegexCacheEntries) _regexCache.Clear();
                _regexCache[pattern] = r;
            }
            return r.IsMatch(value);
        });
        return c;
    }

    /// <summary>Build für Read-Verbindungen (eigene Conn, nicht _conn).</summary>
    private SqliteCommand BuildRead(SqliteConnection conn, string sql, List<SqliteParameter> ps)
    {
        var cmd = new SqliteCommand(sql, conn);
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd;
    }

    private static SqliteParameter Param(string name, object value) => new SqliteParameter(name, value);

    /// <summary>
    /// Sanitisiert FTS5-MATCH-User-Input. FTS5 wirft bei unbalancierten
    /// doppelten Quotes, ungeschlossenen Phrasen oder ungültigen Token-Syntax
    /// einen Syntax-Fehler → 500. Sanitizing-Strategie: Doppelquotes escapen
    /// (`"` → `""`), Phrasen-Breaker (`*`, `:`) entfernen, Rest als Phrase
    /// wickeln (`"input"`) — das ergibt FTS5-Phrase-Query statt Token-Query,
    /// robuster gegen Sonderzeichen. Leer/whitespace → `" "` (no-op-match).
    /// </summary>
    private static string SanitizeFts5(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "\"\"";
        // Doppelquotes escapen + Steuerzeichen entfernen (FTS5-Operator-Syntax
        // wie AND/OR/NOT/NEAR/*/:). Phrasen-Wrap schützt vor unbalancierten
        // Token-Syntax-Fehlern; inner ist bereits escaped.
        string s = input!;
        s = s.Replace("\"", "\"\"");
        // Steuerzeichen entfernen, die FTS5 als Operator/Syntax interpretiert.
        foreach (char c in new[] { '*', ':', '(', ')', '^' }) s = s.Replace(c.ToString(), " ");
        // Kollabieren angrenzender Leerstellen (beim Replace von '*' etc. entstanden).
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(s)) return "\"\"";
        return "\"" + s + "\"";
    }

    private static SpanRow ReadSpan(SqliteDataReader r) => new SpanRow(
        r.GetString(0), r.GetString(1), NStr(r, 2) ?? string.Empty, r.GetString(3), r.GetInt32(4),
        r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), NStr(r, 9),
        NStr(r, 10) ?? "{}", NStr(r, 11) ?? "[]", NStr(r, 12) ?? "{}", NStr(r, 13));

    private static LogRow ReadLog(SqliteDataReader r) => new LogRow(
        r.GetInt64(0), NStr(r, 1), NStr(r, 2), r.GetInt32(3), NStr(r, 4), NStr(r, 5),
        NStr(r, 6) ?? "{}", NStr(r, 7));

    private static string? NStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? NLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (long)r.GetValue(i);
    private static double? NDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (double)r.GetValue(i);

    // -----------------------------------------------------------------------
    // Schema-Bootstrap (SQLite kennt IF NOT EXISTS fuer Tabellen/Indexe/Trigger)
    // -----------------------------------------------------------------------

    private void BootstrapSchema()
    {
        Exec(SqlCreateSpans);
        Exec(SqlCreateLogs);
        Exec(SqlCreateMetrics);
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_spans_trace ON heim_spans(trace_id)");
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_spans_start ON heim_spans(start_unix_nano)");
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_logs_ts ON heim_logs(ts_unix_nano)");
        // TraceId-Filter auf Logs (Join über Trace-Detail) — vorher Full Scan auf
        // der größten Tabelle. Kostenfrei via IF NOT EXISTS (alte DBs migrieren).
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_logs_trace ON heim_logs(trace_id)");
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_metrics_name_ts ON heim_metrics(name, ts_unix_nano)");
        // Unique auf (trace_id, span_id): Retries/Re-Exports erzeugen sonst
        // Duplikatzeilen. Tolerante Migration: vorhandene Duplikate dedup vor
        // Index-Anlage (älteste rowid behalten, jüngere verwerfen — Retry-Semantik:
        // der erste Write gewinnt, spätere Re-Exports sind Replays).
        DeduplicateSpans();
        Exec("CREATE UNIQUE INDEX IF NOT EXISTS idx_heim_spans_trace_span_unique ON heim_spans(trace_id, span_id)");
        // Rollup-Tabelle + Index (Workstream F) — additive, kein user_version-Bump.
        Exec(SqlCreateMetricsRollup);
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_metrics_rollup_name_ts ON heim_metrics_rollup(name, bucket_start)");

        // Serien-Tabelle (Hebel 4): attrs/resource/scope einmal je Serie statt pro
        // Punkt. Additiv + Backfill — bestehende DBs migrieren, frische sind No-Op.
        Exec(SqlCreateMetricSeries);
        EnsureColumn("heim_metrics", "series_id");
        EnsureColumn("heim_metrics_rollup", "series_id");
        BackfillMetricSeries();

        Exec("CREATE VIRTUAL TABLE IF NOT EXISTS heim_spans_fts USING fts5(name, content='heim_spans', content_rowid='rowid')");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_spans_ai AFTER INSERT ON heim_spans BEGIN " +
             "INSERT INTO heim_spans_fts(rowid, name) VALUES (new.rowid, new.name); END");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_spans_ad AFTER DELETE ON heim_spans BEGIN " +
             "INSERT INTO heim_spans_fts(heim_spans_fts, rowid, name) VALUES('delete', old.rowid, old.name); END");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_spans_au AFTER UPDATE ON heim_spans BEGIN " +
             "INSERT INTO heim_spans_fts(heim_spans_fts, rowid, name) VALUES('delete', old.rowid, old.name); " +
             "INSERT INTO heim_spans_fts(rowid, name) VALUES (new.rowid, new.name); END");

        Exec("CREATE VIRTUAL TABLE IF NOT EXISTS heim_logs_fts USING fts5(body, content='heim_logs', content_rowid='rowid')");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_logs_ai AFTER INSERT ON heim_logs BEGIN " +
             "INSERT INTO heim_logs_fts(rowid, body) VALUES (new.rowid, new.body); END");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_logs_ad AFTER DELETE ON heim_logs BEGIN " +
             "INSERT INTO heim_logs_fts(heim_logs_fts, rowid, body) VALUES('delete', old.rowid, old.body); END");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_logs_au AFTER UPDATE ON heim_logs BEGIN " +
             "INSERT INTO heim_logs_fts(heim_logs_fts, rowid, body) VALUES('delete', old.rowid, old.body); " +
             "INSERT INTO heim_logs_fts(rowid, body) VALUES (new.rowid, new.body); END");

        // Attribut-Feldsuche: schluessel-wert-Index ueber Log- UND Resource-Attribute.
        // json_each expandiert attrs_json + resource_json (OTel service.name ist ein
        // Resource-Attr -> in resource_json, nicht attrs_json) in eine Zeile pro
        // (log_rowid, key, value). CAST(value AS TEXT) normalisiert Skalare (String
        // ohne Quotes, Zahl) auf die User-Typsyntax. PRIMARY KEY dedupliziert,
        // OR IGNORE sichert gegen Duplikate bei Insert aus beiden JSON-Spalten.
        Exec("CREATE TABLE IF NOT EXISTS heim_log_attrs (" +
             "log_rowid INTEGER NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, " +
             "PRIMARY KEY (log_rowid, key, value))");
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_log_attrs_kv ON heim_log_attrs(key, value)");
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_log_attrs_log ON heim_log_attrs(log_rowid)");
        // AFTER INSERT: beide JSON-Spalten expandieren (log-Attrs + Resource-Attrs).
        Exec("CREATE TRIGGER IF NOT EXISTS heim_log_attrs_ai AFTER INSERT ON heim_logs BEGIN " +
             "INSERT OR IGNORE INTO heim_log_attrs(log_rowid, key, value) " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.attrs_json) e " +
             "UNION ALL " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.resource_json) e; END");
        // AFTER DELETE: Cascade ueber log_rowid (heim_logs.rowid ist implizit).
        Exec("CREATE TRIGGER IF NOT EXISTS heim_log_attrs_ad AFTER DELETE ON heim_logs BEGIN " +
             "DELETE FROM heim_log_attrs WHERE log_rowid = old.rowid; END");
        // AFTER UPDATE: delete + reinsert (wie FTS5-Pattern).
        Exec("CREATE TRIGGER IF NOT EXISTS heim_log_attrs_au AFTER UPDATE ON heim_logs BEGIN " +
             "DELETE FROM heim_log_attrs WHERE log_rowid = old.rowid; " +
             "INSERT OR IGNORE INTO heim_log_attrs(log_rowid, key, value) " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.attrs_json) e " +
             "UNION ALL " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.resource_json) e; END");

        // Span-Attr-Index: exaktes Pendant zu heim_log_attrs, expandiert Span- UND
        // Resource-Attribute (service.name/service.version sitzen in resource_json)
        // in eine Zeile pro (span_rowid, key, value). Basis fuer Service-/Version-
        // Filter und -Discovery auf Traces. Additiv (CREATE IF NOT EXISTS, kein
        // user_version-Bump); Bestands-DBs fuellt BackfillSpanAttrs() nach.
        Exec("CREATE TABLE IF NOT EXISTS heim_span_attrs (" +
             "span_rowid INTEGER NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, " +
             "PRIMARY KEY (span_rowid, key, value))");
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_span_attrs_kv ON heim_span_attrs(key, value)");
        Exec("CREATE INDEX IF NOT EXISTS idx_heim_span_attrs_span ON heim_span_attrs(span_rowid)");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_span_attrs_ai AFTER INSERT ON heim_spans BEGIN " +
             "INSERT OR IGNORE INTO heim_span_attrs(span_rowid, key, value) " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.attrs_json) e " +
             "UNION ALL " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.resource_json) e; END");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_span_attrs_ad AFTER DELETE ON heim_spans BEGIN " +
             "DELETE FROM heim_span_attrs WHERE span_rowid = old.rowid; END");
        Exec("CREATE TRIGGER IF NOT EXISTS heim_span_attrs_au AFTER UPDATE ON heim_spans BEGIN " +
             "DELETE FROM heim_span_attrs WHERE span_rowid = old.rowid; " +
             "INSERT OR IGNORE INTO heim_span_attrs(span_rowid, key, value) " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.attrs_json) e " +
             "UNION ALL " +
             "SELECT new.rowid, e.key, CAST(e.value AS TEXT) FROM json_each(new.resource_json) e; END");
        BackfillSpanAttrs();
    }

    /// <summary>
    /// Backfill der Span-Attr-Tabelle fuer Bestands-DBs, die vor Anlage von
    /// heim_span_attrs Span-Telemetrie geschrieben haben (Trigger gab es dort
    /// noch nicht). Guard: nur wenn die Attr-Tabelle LEER ist und Spans
    /// existieren — danach halten die Trigger synchron, Retention-Loeschungen
    /// kaskadieren ueber den ad-Trigger. Idempotent dank INSERT OR IGNORE;
    /// auf frischen DBs No-Op.
    /// </summary>
    private void BackfillSpanAttrs()
    {
        try
        {
            // Guard einmalig in C# pruefen (nicht per SQL-WHERE im INSERT): der erste
            // INSERT-Zweig fuellt die Tabelle bereits, ein zweiter WHERE-NOT-EXISTS-
            // Zweig wuerde deshalb nie laufen. Leer -> beide JSON-Spalten nachfuellen.
            long hasAttrs;
            using (var cmd = new SqliteCommand("SELECT EXISTS (SELECT 1 FROM heim_span_attrs LIMIT 1)", _conn))
                hasAttrs = (long)cmd.ExecuteScalar()!;
            if (hasAttrs != 0) return;

            Exec("INSERT OR IGNORE INTO heim_span_attrs(span_rowid, key, value) " +
                 "SELECT s.rowid, e.key, CAST(e.value AS TEXT) FROM heim_spans s, json_each(s.attrs_json) e " +
                 "UNION ALL " +
                 "SELECT s.rowid, e.key, CAST(e.value AS TEXT) FROM heim_spans s, json_each(s.resource_json) e");
        }
        catch
        {
            // Tolerant (Muster BackfillMetricSeries): ein fehlgeschlagener Backfill
            // darf den Bootstrap nicht sprengen — neue Zeilen expandieren die Trigger
            // ab jetzt selbst, nur der Alt-Bestand bliebe unindiziert.
        }
    }

    /// <summary>
    /// Entfernt vorhandene Duplikat-Zeilen in heim_spans vor Anlage des
    /// UNIQUE-Index auf (trace_id, span_id). Retry-Re-Exports können sonst
    /// den UNIQUE-Index-Anlage-Schritt fehlschlagen lassen. Behält die älteste
    /// rowid je (trace_id, span_id), verwirft jüngere (Replay-Semantik:
    /// der erste Write gewinnt, spätere Re-Exports sind idempotent).
    /// Auf einer frischen DB ist das ein Noop (keine Duplikate).
    /// </summary>
    private void DeduplicateSpans()
    {
        try
        {
            Exec("DELETE FROM heim_spans WHERE rowid NOT IN (SELECT MIN(rowid) FROM heim_spans GROUP BY trace_id, span_id)");
        }
        catch
        {
            // Tolerant: wenn Dedup fehlschlägt (z. B. leere Tabelle/Edge-Case),
            // wird der UNIQUE-Index angelegt und schlägt erst beim Insert fehl —
            // dann fangen wir den Insert-Fehler im Write-Pfad ab (OR REPLACE unten).
            // Schlimmstenfalls bleiben Duplikate aus Alt-Beständen, aber die DB
            // bleibt benutzbar (kein Bootstrap-Fehler).
        }
    }

    private void Exec(string sql)
    {
        using var cmd = new SqliteCommand(sql, _conn);
        cmd.ExecuteNonQuery();
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = new SqliteCommand("PRAGMA table_info(" + table + ")", _conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void EnsureColumn(string table, string column)
    {
        if (!HasColumn(table, column))
            Exec("ALTER TABLE " + table + " ADD COLUMN " + column + " INTEGER");
    }

    /// <summary>
    /// Backfill der Serien-Tabelle (Hebel 4) fuer bestehende DBs: dedupliziert
    /// attrs/resource/scope aus heim_metrics + heim_metrics_rollup in
    /// heim_metric_series und setzt series_id auf den Metrik-Zeilen. Idempotent —
    /// auf einer frischen DB (Write-Pfad schreibt series_id direkt) No-Op.
    /// </summary>
    private void BackfillMetricSeries()
    {
        try
        {
            Exec("INSERT OR IGNORE INTO heim_metric_series (name, attrs_json, resource_json, scope_name, scope_version) " +
                 "SELECT DISTINCT name, COALESCE(attrs_json,'{}'), COALESCE(resource_json,'{}'), COALESCE(scope_name,''), COALESCE(scope_version,'') " +
                 "FROM heim_metrics WHERE series_id IS NULL");
            Exec("INSERT OR IGNORE INTO heim_metric_series (name, attrs_json, resource_json, scope_name, scope_version) " +
                 "SELECT DISTINCT name, COALESCE(attrs_json,'{}'), COALESCE(resource_json,'{}'), COALESCE(scope_name,''), COALESCE(scope_version,'') " +
                 "FROM heim_metrics_rollup WHERE series_id IS NULL");
            Exec("UPDATE heim_metrics SET series_id = (SELECT s.series_id FROM heim_metric_series s " +
                 "WHERE s.name = heim_metrics.name AND s.attrs_json = COALESCE(heim_metrics.attrs_json,'{}') " +
                 "AND s.resource_json = COALESCE(heim_metrics.resource_json,'{}') " +
                 "AND s.scope_name = COALESCE(heim_metrics.scope_name,'') AND s.scope_version = COALESCE(heim_metrics.scope_version,'')) " +
                 "WHERE series_id IS NULL");
            Exec("UPDATE heim_metrics_rollup SET series_id = (SELECT s.series_id FROM heim_metric_series s " +
                 "WHERE s.name = heim_metrics_rollup.name AND s.attrs_json = COALESCE(heim_metrics_rollup.attrs_json,'{}') " +
                 "AND s.resource_json = COALESCE(heim_metrics_rollup.resource_json,'{}') " +
                 "AND s.scope_name = COALESCE(heim_metrics_rollup.scope_name,'') AND s.scope_version = COALESCE(heim_metrics_rollup.scope_version,'')) " +
                 "WHERE series_id IS NULL");
        }
        catch
        {
            // Tolerant: Backfill ist rein additiv. Schlägt er fehl (z. B. sehr alte
            // DB ohne attrs_json), bleibt die DB benutzbar — neue Writes setzen
            // series_id, Discovery liest weiterhin die alten Spalten (JOIN fällt
            // auf NULL-series_id-Zeilen auf attrs_json zurück, s. MetricSource).
        }
    }

    internal void SweepRetention()
    {
        // Guard by timer (SweepActive), aber doppelt-halten: auch ein manuell
        // angerufener Sweep (z. B. durch Tests) respektiert die Konfiguration.
        if (!_options.AnyTimeRetention && _options.MaxBytes <= 0) return;
        var sw = Stopwatch.StartNew();   // C3: Sweep-Latenz (host.sweep.duration)
        lock (_gate)
        {
            try
            {
                bool anyDeleted = false;
                // 0. Rollup (Workstream F): rohe Metrik-Punkte aelter als RawDays zu
                //    ResolutionSeconds-Buckets aggregieren, VOR der TTL-Loeschung.
                //    Liefert true, falls Raw-Zeilen eingrollt wurden (-> Reclaim).
                if (RollupRawMetrics()) anyDeleted = true;
                // 1. Zeitbasierte Retention pro Signal (A1). Tabelle nur anfassen,
                // wenn ihr effektiver Wert > 0 (0 = unbegrenzt).
                if (_options.TracesDaysEffective > 0)
                {
                    long n = DeleteByCutoff("heim_spans", "start_unix_nano", _options.TracesDaysEffective);
                    Interlocked.Add(ref _retDeletedSpans, n); anyDeleted |= n > 0;
                }
                if (_options.LogsDaysEffective > 0)
                {
                    long n = DeleteByCutoff("heim_logs", "ts_unix_nano", _options.LogsDaysEffective);
                    Interlocked.Add(ref _retDeletedLogs, n); anyDeleted |= n > 0;
                }
                if (_options.MetricsDaysEffective > 0)
                {
                    long n = DeleteByCutoff("heim_metrics", "ts_unix_nano", _options.MetricsDaysEffective);
                    // Rollup-Zeilen altern mit derselben Metric-Frist (bucket_start
                    // als Zeit-Spalte) — in denselben metrics-Counter gefaltet.
                    n += DeleteByCutoff("heim_metrics_rollup", "bucket_start", _options.MetricsDaysEffective);
                    Interlocked.Add(ref _retDeletedMetrics, n); anyDeleted |= n > 0;
                }

                // 2. Größen-Cap mit signalübergreifender Eviction (A2).
                if (_options.MaxBytes > 0) anyDeleted |= EvictByCap();

                // 2.5. Verwaiste Serien (Hebel 4): nach Metrik-Loeschung (TTL oder
                // Cap) aufraeumen. NOT IN auf der kleinen Serien-Tabelle ist billig;
                // idempotent, laeuft nur wenn ueberhaupt geloescht wurde.
                if (anyDeleted)
                {
                    Exec("DELETE FROM heim_metric_series WHERE series_id NOT IN " +
                         "(SELECT DISTINCT series_id FROM heim_metrics) AND series_id NOT IN " +
                         "(SELECT DISTINCT series_id FROM heim_metrics_rollup)");
                }

                // 3. Space-Reclaim (A3) — NUR wenn gelöscht wurde: FTS5-Shadow-
                // Tabellen geben Seiten bei DELETE nicht frei (Tombstones bleiben
                // in den Segmenten), darum rebuild + incremental_vacuum, sonst
                // würde die Datei über die Tombstones monoton wachsen. Base-Pages
                // sind danach ebenfalls zurückgewonnen; die Datei schrumpft.
                if (anyDeleted)
                {
                    RebuildFtsIndexes();
                    RunIncrementalVacuum();
                }
            }
            catch { /* Sweeper darf nie den Host killen */ }
        }
        sw.Stop();
        Interlocked.Exchange(ref _hostSweepDurationTicks, sw.Elapsed.Ticks);   // C3: letzter Sweep-Wert
    }

    // FTS5-Indizes aus den aktuellen Base-Tabellen neu aufbauen (befreit die
    // alten Segment-Pages mit ihren Tombstones → landen auf der Freelist →
    // incremental_vacuum gibt sie an die Datei zurück).
    private void RebuildFtsIndexes()
    {
        Exec("INSERT INTO heim_spans_fts(heim_spans_fts) VALUES('rebuild');");
        Exec("INSERT INTO heim_logs_fts(heim_logs_fts) VALUES('rebuild');");
    }

    // Loescht Zeilen aelter als `days` aus `table` (Zeit-Spalte `timeCol`,
    // indexgestuetzt) und liefert die Anzahl geloeschter Zeilen (-> A4-Zaehler).
    private long DeleteByCutoff(string table, string timeCol, int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds() * 1_000_000_000L;
        using var tx = _conn.BeginTransaction();
        using var cmd = new SqliteCommand($"DELETE FROM {table} WHERE {timeCol} < @c", _conn, tx);
        cmd.Parameters.AddWithValue("@c", cutoff);
        long n = cmd.ExecuteNonQuery();
        tx.Commit();
        return n;
    }

    // A2: aelteste Zeilen signaluebergreifend evicten, bis die belegte DB-Groesse
    // unter den Ziel-Fuellgrad (90 % von MaxBytes) sinkt. Gemessen wird an
    // BELEGTEN Pages (page_count - freelist_count) * page_size — nicht an der
    // Dateigroesse: Base-Pages landen bei DELETE sofort auf der Freelist (die
    // Schleife kommt runter), FTS5-Segment-Pages erst nach RebuildFtsIndexes.
    // Liefert true, falls Zeilen evictet wurden (-> A4-Zaehler + Reclaim).
    private bool EvictByCap()
    {
        if (UsedBytes() <= _options.MaxBytes) return false;
        const int tranche = 1000;
        long target = _options.MaxBytes * 9 / 10;   // 90 % Ziel-Fuellgrad (Puffer)
        bool evicted = false;
        while (UsedBytes() > target)
        {
            var oldest = OldestRows(tranche);
            if (oldest.Count == 0) break;           // DB leer — nichts mehr zu evicten
            long deleted = 0;
            using (var tx = _conn.BeginTransaction())
            {
                foreach (var g in oldest.GroupBy(r => r.Src))
                {
                    var ids = string.Join(',', g.Select(r => r.Rowid.ToString(CultureInfo.InvariantCulture)));
                    using var cmd = new SqliteCommand(
                        $"DELETE FROM {SourceTable(g.Key)} WHERE rowid IN ({ids})", _conn, tx);
                    deleted += cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            // Eviction-Zaehler pro Signal (A4).
            foreach (var g in oldest.GroupBy(r => r.Src))
            {
                long n = g.Count();
                switch (g.Key)
                {
                    case "spans": Interlocked.Add(ref _retEvictedSpans, n); break;
                    case "logs": Interlocked.Add(ref _retEvictedLogs, n); break;
                    case "metrics":
                    case "metrics_rollup": Interlocked.Add(ref _retEvictedMetrics, n); break;  // gefaltet (Workstream F)
                }
            }
            if (deleted == 0) break;                // Sicherheitsbremse
            evicted = true;
        }
        return evicted;
    }

    // Aelteste `k` Zeilen ueber alle drei Signal-Tabellen (geordnet nach Zeit).
    private List<(long Rowid, string Src)> OldestRows(int k)
    {
        const string sql =
            "SELECT rowid, src FROM (" +
            "SELECT rowid, start_unix_nano AS t, 'spans' AS src FROM heim_spans " +
            "UNION ALL SELECT rowid, ts_unix_nano, 'logs' FROM heim_logs " +
            "UNION ALL SELECT rowid, ts_unix_nano, 'metrics' FROM heim_metrics " +
            "UNION ALL SELECT rowid, bucket_start, 'metrics_rollup' FROM heim_metrics_rollup) " +
            "ORDER BY t ASC LIMIT @k";
        var list = new List<(long, string)>();
        using var cmd = new SqliteCommand(sql, _conn);
        cmd.Parameters.AddWithValue("@k", k);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    private static string SourceTable(string src) => src switch
    {
        "spans" => "heim_spans",
        "logs" => "heim_logs",
        "metrics" => "heim_metrics",
        "metrics_rollup" => "heim_metrics_rollup",
        _ => throw new InvalidOperationException("Unbekannte Signal-Quelle: " + src)
    };

    // Belegte DB-Groesse = (page_count - freelist_count) * page_size.
    internal long UsedBytes() =>
        (PragmaLong("page_count") - PragmaLong("freelist_count")) * PragmaLong("page_size");

    // A3: Free-Pages nach DELETE/Eviction an die Datei zurueckgeben (Datei
    // schrumpft). Nur wirksam bei auto_vacuum=INCREMENTAL (==2), sonst no-op.
    private void RunIncrementalVacuum()
    {
        if (!_options.AutoVacuum) return;
        if (PragmaLong("auto_vacuum") != 2) return;
        Exec("PRAGMA incremental_vacuum;");
    }

    // A3: auto_vacuum vor Tabellen-Anlage setzen (frische DB) bzw. eine Legacy-
    // DB (user_version=0, auto_vacuum=0) einmalig per VACUUM migrieren.
    private void BootstrapAutoVacuum()
    {
        int existingTables = Convert.ToInt32(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table'"));
        if (existingTables == 0)
        {
            // Frische DB: auto_vacuum wirkt jetzt (vor BootstrapSchema).
            if (_options.AutoVacuum) Exec("PRAGMA auto_vacuum = INCREMENTAL;");
            return;
        }
        // Bestehende DB: nur migrieren, wenn noch auf Legacy-Stand (user_version=0).
        if (PragmaLong("user_version") > 0) return;
        if (!_options.AutoVacuum) return;               // Operator will kein Reclaim.
        int av = (int)PragmaLong("auto_vacuum");
        if (av == 0)
        {
            if (!_options.VacuumMigrateLegacy) return;  // Notaus: bleibt migrierbar.
            Exec("PRAGMA auto_vacuum = INCREMENTAL;");
            Exec("VACUUM;");                            // teuer/exklusiv, einmalig
        }
        // user_version=1 markiert: auto_vacuum steht (gesetzt oder via VACUUM).
        Exec("PRAGMA user_version = 1;");
    }

    internal long PragmaLong(string name)
    {
        using var cmd = new SqliteCommand($"PRAGMA {name};", _conn);
        return Convert.ToInt64(cmd.ExecuteScalar()!);
    }

    private object? Scalar(string sql)
    {
        using var cmd = new SqliteCommand(sql, _conn);
        return cmd.ExecuteScalar();
    }

    private static long NowUnixNano => DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1_000_000_000L;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _retentionTimer?.Dispose();   // stoppt neue Callbacks (in-flight Sweep hält _gate)
        // C4: Commands + Verbindung unter _gate disposen, damit kein in-flight
        // Write (das _conn/Commands benutzt) halbfertig den Teppich weggezogen
        // bekommt. Write* prüft nach Lock-Aquisition nochmals _disposed (Double-Check).
        lock (_gate)
        {
            _insSpan.Dispose(); _insLog.Dispose(); _insMetric.Dispose();
            _insSeries.Dispose(); _selSeriesId.Dispose();
            _conn.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // SQL-Konstanten
    // -----------------------------------------------------------------------

    private const string SqlCreateSpans =
        "CREATE TABLE IF NOT EXISTS heim_spans (" +
        "trace_id TEXT NOT NULL, span_id TEXT NOT NULL, parent_id TEXT, " +
        "name TEXT NOT NULL, kind INTEGER NOT NULL, " +
        "start_unix_nano INTEGER NOT NULL, end_unix_nano INTEGER NOT NULL, duration_ns INTEGER NOT NULL, " +
        "status_code INTEGER NOT NULL, status_msg TEXT, " +
        "attrs_json TEXT, events_json TEXT, links_json TEXT, " +
        "resource_json TEXT, scope_name TEXT, scope_version TEXT)";

    private const string SqlCreateLogs =
        "CREATE TABLE IF NOT EXISTS heim_logs (" +
        "ts_unix_nano INTEGER NOT NULL, trace_id TEXT, span_id TEXT, " +
        "severity INTEGER NOT NULL, severity_text TEXT, body TEXT, " +
        "attrs_json TEXT, resource_json TEXT, scope_name TEXT, scope_version TEXT)";

    private const string SqlCreateMetrics =
        "CREATE TABLE IF NOT EXISTS heim_metrics (" +
        "name TEXT NOT NULL, unit TEXT, type INTEGER NOT NULL, temporality INTEGER NOT NULL, ts_unix_nano INTEGER NOT NULL, " +
        "value REAL NOT NULL, count INTEGER, sum REAL, min REAL, max REAL, " +
        "bucket_counts_json TEXT, explicit_bounds_json TEXT, " +
        "attrs_json TEXT, resource_json TEXT, scope_name TEXT, scope_version TEXT, " +
        "series_id INTEGER)";

    // Serien-Tabelle (Hebel 4): attrs/resource/scope einmal je Serie statt pro
    // Metrik-Punkt. UNIQUE(name, attrs_json, resource_json, scope_name, scope_version)
    // macht INSERT OR IGNORE idempotent; series_id ist der Fingerprint.
    private const string SqlCreateMetricSeries =
        "CREATE TABLE IF NOT EXISTS heim_metric_series (" +
        "series_id INTEGER PRIMARY KEY AUTOINCREMENT, " +
        "name TEXT NOT NULL, attrs_json TEXT NOT NULL, resource_json TEXT NOT NULL, " +
        "scope_name TEXT NOT NULL DEFAULT '', scope_version TEXT NOT NULL DEFAULT '', " +
        "UNIQUE(name, attrs_json, resource_json, scope_name, scope_version))";

    private const string SqlInsertSeries =
        "INSERT OR IGNORE INTO heim_metric_series (name, attrs_json, resource_json, scope_name, scope_version) " +
        "VALUES (@p0,@p1,@p2,@p3,@p4)";

    private const string SqlSelectSeriesId =
        "SELECT series_id FROM heim_metric_series " +
        "WHERE name=@n AND attrs_json=@a AND resource_json=@r AND scope_name=@sn AND scope_version=@sv";

    // Rollup-Tabelle (Workstream F): Spiegel von heim_metrics Wert-Spalten, aber
    // ts_unix_nano -> bucket_start + resolution_seconds. Eine Zeile pro
    // (name, Fingerprint, bucket). Impliziter rowid (kein PK). Additive Tabelle
    // (CREATE IF NOT EXISTS, kein user_version-Bump) — Legacy-DBs legen sie nach
    // der VACUUM-Migration frisch an (korrekt, s. Plan).
    private const string SqlCreateMetricsRollup =
        "CREATE TABLE IF NOT EXISTS heim_metrics_rollup (" +
        "name TEXT NOT NULL, unit TEXT, type INTEGER NOT NULL, temporality INTEGER NOT NULL, " +
        "bucket_start INTEGER NOT NULL, resolution_seconds INTEGER NOT NULL, " +
        "value REAL NOT NULL, count INTEGER, sum REAL, min REAL, max REAL, " +
        "bucket_counts_json TEXT, explicit_bounds_json TEXT, " +
        "attrs_json TEXT, resource_json TEXT, scope_name TEXT, scope_version TEXT, " +
        "series_id INTEGER)";

    private const string SqlInsertSpan =
        "INSERT OR IGNORE INTO heim_spans (trace_id, span_id, parent_id, name, kind, " +
        "start_unix_nano, end_unix_nano, duration_ns, status_code, status_msg, " +
        "attrs_json, events_json, links_json, resource_json, scope_name, scope_version) " +
        "VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15)";

    private const string SqlInsertLog =
        "INSERT INTO heim_logs (ts_unix_nano, trace_id, span_id, severity, severity_text, body, " +
        "attrs_json, resource_json, scope_name, scope_version) " +
        "VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9)";

    private const string SqlInsertMetric =
        "INSERT INTO heim_metrics (name, unit, type, temporality, ts_unix_nano, " +
        "value, count, sum, min, max, bucket_counts_json, explicit_bounds_json, " +
        "attrs_json, resource_json, scope_name, scope_version, series_id) " +
        "VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16)";
}