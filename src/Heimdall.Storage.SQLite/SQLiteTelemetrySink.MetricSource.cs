using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Heimdall;
using Microsoft.Data.Sqlite;

namespace Heimdall.Storage.SQLite;

// ---------------------------------------------------------------------------
// IHeimdallMetricSource fuer das SQLite-Backend (partial der Sink).
// Label-Discovery + Punkt-Fetch ueber heim_metrics; attrs_json/resource_json
// via System.Text.Json. Parametrisierte Queries (name IN (@p0,@p1,…)), hinter
// _gate. Matcher in-app gefiltert — Paritaet zum Walhalla-Backend.
// ---------------------------------------------------------------------------

public sealed partial class SQLiteTelemetrySink : IHeimdallMetricSource
{
    private const int SourceScanCap = 50000;

    public IReadOnlyList<string> ListMetricNames(long? fromUnixNano = null, long? toUnixNano = null)
    {
        var sb = SqlBuilder();
        sb.Append("SELECT DISTINCT name FROM heim_metrics WHERE 1=1");
        var ps = new List<SqliteParameter>();
        if (fromUnixNano is not null) { sb.Append(" AND ts_unix_nano >= @from"); ps.Add(Param("@from", fromUnixNano.Value)); }
        if (toUnixNano is not null) { sb.Append(" AND ts_unix_nano <= @to"); ps.Add(Param("@to", toUnixNano.Value)); }
        // Workstream F: bei aktivem Rollup auch die Rollup-Tabelle vereinigen —
        // sonst verschwindet ein Name, sobald seine Raw-Punkte alle gealtert sind.
        if (_options.RollupEnabledEffective)
        {
            sb.Append(" UNION SELECT DISTINCT name FROM heim_metrics_rollup WHERE 1=1");
            if (fromUnixNano is not null) { sb.Append(" AND bucket_start >= @from2"); ps.Add(Param("@from2", fromUnixNano.Value)); }
            if (toUnixNano is not null) { sb.Append(" AND bucket_start <= @to2"); ps.Add(Param("@to2", toUnixNano.Value)); }
        }
        sb.Append(" ORDER BY name");

        var names = new List<string>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) names.Add(r.GetString(0));
        // heimdall.*-Observability-Metriken (A4/C3) — „now"-Metriken, immer gelistet
        // (unabhaengig vom Zeitfenster, da sie den Live-Zustand beschreiben).
        names.Add(MRetentionDeleted);
        names.Add(MRetentionEvicted);
        names.Add(MStorageBytes);
        names.Add(MStorageRows);
        names.Add(MHostIngest);
        names.Add(MHostSweepDuration);
        return names;
    }

    public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? matchers = null,
                                                 long? fromUnixNano = null, long? toUnixNano = null)
    {
        var names = new SortedSet<string>();
        foreach (var (attrs, res) in ScanLabelRows(fromUnixNano, toUnixNano))
        {
            var labels = ParseLabels(attrs, res);
            if (!Matches(labels, matchers)) continue;
            foreach (var k in labels.Keys) names.Add(k);
        }
        return new List<string>(names);
    }

    public IReadOnlyList<string> ListLabelValues(string labelName,
                                                 IReadOnlyList<HLabelMatcher>? matchers = null,
                                                 long? fromUnixNano = null, long? toUnixNano = null)
    {
        if (string.IsNullOrEmpty(labelName)) return Array.Empty<string>();
        var values = new SortedSet<string>();
        foreach (var (attrs, res) in ScanLabelRows(fromUnixNano, toUnixNano))
        {
            var labels = ParseLabels(attrs, res);
            if (!Matches(labels, matchers)) continue;
            if (labels.TryGetValue(labelName, out var v)) values.Add(v);
        }
        return new List<string>(values);
    }

    public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery query)
    {
        if (query is null || query.Names is null || query.Names.Count == 0)
            return Array.Empty<HMetricPointView>();

        // heimdall.*-Observability-Metriken (A4) synthetisieren; reelle OTel-Namen
        // ueber heim_metrics holen. Ein Query darf beide mischen.
        var heimdall = new List<string>();
        var real = new List<string>();
        foreach (var n in query.Names)
        {
            if (IsHeimdallMetric(n)) heimdall.Add(n);
            else real.Add(n);
        }

        var result = new List<HMetricPointView>();
        if (heimdall.Count > 0)
            result.AddRange(SynthesizeHeimdallMetrics(heimdall, query.Matchers));
        if (real.Count > 0)
            result.AddRange(FetchRealPoints(real, query));
        return result;
    }

    // Reelle Metrik-Punkte aus heim_metrics (alter FetchPoints-Koerper, auf
    // `names` statt query.Names parametrisiert). Bei aktivem Rollup (Workstream F)
    // UNION ALL mit heim_metrics_rollup (bucket_start AS ts_unix_nano) — disjunktiv
    // konstruktiv (eine Rollup-Zeile entsteht nur, wenn ihre Raw-Zeilen geloescht
    // wurden), darum KEIN Boundary-Filter (sonst Gefahr, noch nicht gerollte Raw-
    // Punkte auszuschliessen, wenn die Query-Boundary der Sweep-Boundary voraus ist).
    private IReadOnlyList<HMetricPointView> FetchRealPoints(IReadOnlyList<string> names, HMetricQuery query)
    {
        var sb = SqlBuilder();
        sb.Append("SELECT name, unit, type, temporality, ts_unix_nano, value, count, sum, min, max, " +
                  "bucket_counts_json, explicit_bounds_json, attrs_json, resource_json, scope_name " +
                  "FROM heim_metrics WHERE name IN (");
        var ps = new List<SqliteParameter>();
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var pname = "@n" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(pname);
            ps.Add(Param(pname, names[i]));
        }
        sb.Append(')');
        if (query.FromUnixNano is not null) { sb.Append(" AND ts_unix_nano >= @from"); ps.Add(Param("@from", query.FromUnixNano.Value)); }
        if (query.ToUnixNano is not null) { sb.Append(" AND ts_unix_nano <= @to"); ps.Add(Param("@to", query.ToUnixNano.Value)); }
        if (_options.RollupEnabledEffective)
        {
            sb.Append(" UNION ALL SELECT name, unit, type, temporality, bucket_start, value, count, sum, min, max, " +
                      "bucket_counts_json, explicit_bounds_json, attrs_json, resource_json, scope_name " +
                      "FROM heim_metrics_rollup WHERE name IN (");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var pname = "@r" + i.ToString(CultureInfo.InvariantCulture);
                sb.Append(pname);
                ps.Add(Param(pname, names[i]));
            }
            sb.Append(')');
            if (query.FromUnixNano is not null) { sb.Append(" AND bucket_start >= @from2"); ps.Add(Param("@from2", query.FromUnixNano.Value)); }
            if (query.ToUnixNano is not null) { sb.Append(" AND bucket_start <= @to2"); ps.Add(Param("@to2", query.ToUnixNano.Value)); }
        }
        sb.Append(" ORDER BY name, ts_unix_nano ASC LIMIT @lim");
        ps.Add(Param("@lim", Math.Max(1, query.Limit)));

        var list = new List<HMetricPointView>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var labels = ParseLabels(NStr(r, 12), NStr(r, 13));
                if (!Matches(labels, query.Matchers)) continue;
                list.Add(new HMetricPointView(
                    r.GetString(0), NStr(r, 1), (HMetricType)r.GetInt32(2), (HTemporality)r.GetInt32(3), r.GetInt64(4), r.GetDouble(5),
                    NLong(r, 6), NDouble(r, 7), NDouble(r, 8), NDouble(r, 9),
                    ParseLongs(NStr(r, 10)), ParseDoubles(NStr(r, 11)),
                    labels, NStr(r, 14)));
            }
        }
        return list;
    }

    private IEnumerable<(string? attrs, string? res)> ScanLabelRows(long? fromUnixNano, long? toUnixNano)
    {
        var sb = SqlBuilder();
        sb.Append("SELECT attrs_json, resource_json FROM heim_metrics WHERE 1=1");
        var ps = new List<SqliteParameter>();
        if (fromUnixNano is not null) { sb.Append(" AND ts_unix_nano >= @from"); ps.Add(Param("@from", fromUnixNano.Value)); }
        if (toUnixNano is not null) { sb.Append(" AND ts_unix_nano <= @to"); ps.Add(Param("@to", toUnixNano.Value)); }
        // Workstream F: bei aktivem Rollup Labels beider Tabellen vereinigen —
        // Labels ueberleben fuer voll gealterte Metriken (Cap unverändert).
        if (_options.RollupEnabledEffective)
        {
            sb.Append(" UNION ALL SELECT attrs_json, resource_json FROM heim_metrics_rollup WHERE 1=1");
            if (fromUnixNano is not null) { sb.Append(" AND bucket_start >= @from2"); ps.Add(Param("@from2", fromUnixNano.Value)); }
            if (toUnixNano is not null) { sb.Append(" AND bucket_start <= @to2"); ps.Add(Param("@to2", toUnixNano.Value)); }
        }
        sb.Append(" LIMIT @cap");
        ps.Add(Param("@cap", SourceScanCap));

        var rows = new List<(string?, string?)>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) rows.Add((NStr(r, 0), NStr(r, 1)));
        return rows;
    }

    private static bool Matches(IReadOnlyDictionary<string, string> labels, IReadOnlyList<HLabelMatcher>? matchers)
    {
        if (matchers is null || matchers.Count == 0) return true;
        for (int i = 0; i < matchers.Count; i++)
        {
            var m = matchers[i];
            labels.TryGetValue(m.Name, out var v);
            switch (m.Op)
            {
                case HMatchOp.Eq:
                    if (!string.Equals(v ?? string.Empty, m.Value, StringComparison.Ordinal)) return false;
                    break;
                case HMatchOp.Ne:
                    if (string.Equals(v ?? string.Empty, m.Value, StringComparison.Ordinal)) return false;
                    break;
                case HMatchOp.Re:
                    if (v is null || !RegexCache.IsMatch(v, m.Value)) return false;
                    break;
                case HMatchOp.Nre:
                    if (v is not null && RegexCache.IsMatch(v, m.Value)) return false;
                    break;
            }
        }
        return true;
    }

    private static Dictionary<string, string> ParseLabels(string? attrsJson, string? resourceJson)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeLabels(dict, resourceJson);
        MergeLabels(dict, attrsJson);
        return dict;
    }

    private static void MergeLabels(Dictionary<string, string> dict, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Null) continue;
                dict[prop.Name] = JsonValueToString(prop.Value);
            }
        }
        catch { /* kaputtes JSON ignorieren */ }
    }

    private static string JsonValueToString(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString() ?? string.Empty;
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Number: return el.GetRawText();
            default: return el.GetRawText();
        }
    }

    private static IReadOnlyList<long>? ParseLongs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var arr = new long[doc.RootElement.GetArrayLength()];
            int i = 0;
            foreach (var el in doc.RootElement.EnumerateArray()) arr[i++] = el.GetInt64();
            return arr;
        }
        catch { return null; }
    }

    private static IReadOnlyList<double>? ParseDoubles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        // Alt-Bestand enthielt das ungültige Literal „Infinity" (ToString("R") von
        // +Inf) — JsonDocument.Parse lehnt das ab. Wir normalisieren es vorab auf
        // das JSON-valide String-Token „+Inf", damit auch alte Zeilen gelesen werden.
        if (json.Contains("Infinity", StringComparison.Ordinal))
            json = json.Replace("Infinity", "\"+Inf\"", StringComparison.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var arr = new double[doc.RootElement.GetArrayLength()];
            int i = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                arr[i++] = el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString() switch
                    {
                        "+Inf" => double.PositiveInfinity,
                        "-Inf" => double.NegativeInfinity,
                        _ => double.NaN,
                    },
                    JsonValueKind.Number => el.GetDouble(),
                    _ => double.NaN,
                };
            }
            return arr;
        }
        catch { return null; }
    }

    private static class RegexCache
    {
        private const int MaxEntries = 256;
        private static readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);
        public static bool IsMatch(string input, string pattern)
        {
            Regex r;
            lock (_cache)
            {
                if (!_cache.TryGetValue(pattern, out r!))
                {
                    r = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                    if (_cache.Count >= MaxEntries) _cache.Clear();
                    _cache[pattern] = r;
                }
            }
            return r.IsMatch(input);
        }
    }

    // -----------------------------------------------------------------------
    // A4: heimdall.*-Observability-Metriken (synthetisiert, nicht in heim_metrics
    // gespeichert — keine rekursive Selbst-Befuellung). Der Sink ist via
    // CompositeMetricSource als _real ans Prom-Layer angebunden; heimdall.*-Namen
    // routen dort an _real.FetchPoints. Labels: signal=spans|logs|metrics.
    // 1.0-Limitation: „now"-Punkte — historische Range-Queries sehen sie nur am
    // aktuellen Rand, nicht in der Vergangenheit.
    // -----------------------------------------------------------------------

    private const string MRetentionDeleted = "heimdall.retention.deleted";
    private const string MRetentionEvicted = "heimdall.retention.evicted";
    private const string MStorageBytes = "heimdall.storage.bytes";
    private const string MStorageRows = "heimdall.storage.rows";
    private const string MHostIngest = "heimdall.host.ingest";
    private const string MHostSweepDuration = "heimdall.host.sweep.duration";
    private const string LSignal = "signal";
    private const string ScopeHeimdall = "heimdall";

    private static readonly HashSet<string> HeimdallMetricNames = new(StringComparer.Ordinal)
    {
        MRetentionDeleted, MRetentionEvicted, MStorageBytes, MStorageRows,
        MHostIngest, MHostSweepDuration
    };

    private static bool IsHeimdallMetric(string name) => HeimdallMetricNames.Contains(name);

    private IReadOnlyList<HMetricPointView> SynthesizeHeimdallMetrics(
        IReadOnlyList<string> names, IReadOnlyList<HLabelMatcher>? matchers)
    {
        var list = new List<HMetricPointView>();
        long now = NowUnixNano;
        foreach (var n in names)
        {
            switch (n)
            {
                case MRetentionDeleted:
                    AddSignalPoints(list, n, now, matchers, HMetricType.Sum, HTemporality.Cumulative,
                        Interlocked.Read(ref _retDeletedSpans),
                        Interlocked.Read(ref _retDeletedLogs),
                        Interlocked.Read(ref _retDeletedMetrics));
                    break;
                case MRetentionEvicted:
                    AddSignalPoints(list, n, now, matchers, HMetricType.Sum, HTemporality.Cumulative,
                        Interlocked.Read(ref _retEvictedSpans),
                        Interlocked.Read(ref _retEvictedLogs),
                        Interlocked.Read(ref _retEvictedMetrics));
                    break;
                case MStorageBytes:
                {
                    var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (Matches(labels, matchers))
                        list.Add(new HMetricPointView(n, "By", HMetricType.Gauge, HTemporality.Unspecified,
                            now, UsedBytes(), null, null, null, null, null, null, labels, ScopeHeimdall));
                    break;
                }
                case MStorageRows:
                    AddSignalPoints(list, n, now, matchers, HMetricType.Gauge, HTemporality.Unspecified,
                        CountSpans(), CountLogs(), CountMetrics());
                    break;
                case MHostIngest:
                    // C3: ingested-Volume pro Signal (Sum/Cumulative; Prom → *_total).
                    AddSignalPoints(list, n, now, matchers, HMetricType.Sum, HTemporality.Cumulative,
                        Interlocked.Read(ref _hostIngestSpans),
                        Interlocked.Read(ref _hostIngestLogs),
                        Interlocked.Read(ref _hostIngestMetrics));
                    break;
                case MHostSweepDuration:
                {
                    // C3: Latenz des letzten realen Sweeps (Gauge, Sekunden; Prom → *_seconds).
                    var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (Matches(labels, matchers))
                    {
                        double seconds = new TimeSpan(Interlocked.Read(ref _hostSweepDurationTicks)).TotalSeconds;
                        list.Add(new HMetricPointView(n, "s", HMetricType.Gauge, HTemporality.Unspecified,
                            now, seconds, null, null, null, null, null, null, labels, ScopeHeimdall));
                    }
                    break;
                }
            }
        }
        return list;
    }

    // Ein Punkt pro Signal (spans/logs/metrics) mit signal-Label; matcher-gefiltert.
    private static void AddSignalPoints(List<HMetricPointView> list, string name, long now,
        IReadOnlyList<HLabelMatcher>? matchers, HMetricType type, HTemporality temp,
        long spans, long logs, long metrics)
    {
        var signals = new[] { ("spans", spans), ("logs", logs), ("metrics", metrics) };
        foreach (var (sig, val) in signals)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal) { [LSignal] = sig };
            if (!Matches(labels, matchers)) continue;
            list.Add(new HMetricPointView(name, null, type, temp, now, val,
                null, null, null, null, null, null, labels, ScopeHeimdall));
        }
    }
}