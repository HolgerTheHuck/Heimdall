using System.Collections.Generic;
using System.IO;
using Heimdall;
using Microsoft.Data.Sqlite;

namespace Heimdall.Storage.SQLite;

/// <summary>
/// Depot-Übersicht (Storage-Stats) für die UI: Dateigrößen (DB + WAL), Page-
/// Statistik (page_size/page_count/freelist), Verbrauch je Tabelle und die
/// effektive Retention-/Cap-Konfiguration. Rein lesend.
///
/// Zwei Größen-Strategien: Wenn die gebundene SQLite-Bibliothek <c>dbstat</c>
/// anbietet (SQLITE_ENABLE_DBSTAT_VTAB), werden echte Page-Größen je Objekt
/// gelesen (inkl. Indexe/FTS-Shadow). Das gebündelte e_sqlite3 von
/// Microsoft.Data.Sqlite hat dbstat NICHT — dann fällt die Statistik auf die
/// <b>logische Datenmenge</b> je Tabelle zurück (SUM(LENGTH(...)) über alle
/// Spalten): exakt genug für die Frage „welche Tabelle frisst das Volumen",
/// in der UI als Schätzung gekennzeichnet (DbStatAvailable = false).
/// </summary>
public sealed partial class SQLiteTelemetrySink
{
    /// <summary>
    /// Depot-Statistik sammeln. Kein Lock: alle Zugriffe sind lesend
    /// (Read-Connection WAL-konkurrent, PRAGMAs auf der Write-Connection).
    /// </summary>
    public DepotStats GetDepotStats()
    {
        long pageSize = PragmaLong("page_size");
        long pageCount = PragmaLong("page_count");
        long freelist = PragmaLong("freelist_count");
        long fileBytes = FileSizeBytes(_options.DataPath);
        long walBytes = FileSizeBytes(_options.DataPath + "-wal");

        var tables = CollectTableStats(out bool dbStat);

        return new DepotStats(
            FileBytes: fileBytes,
            WalBytes: walBytes,
            PageSize: pageSize,
            PageCount: pageCount,
            FreelistCount: freelist,
            FreelistBytes: freelist * pageSize,
            UsedBytes: UsedBytes(),
            TotalLogicalBytes: pageCount * pageSize,
            Tables: tables,
            DbStatAvailable: dbStat,
            CompressionEnabled: true,           // Format v2: JSON-Spalten immer Brotli-BLOB
            CompressedStoredBytes: System.Threading.Interlocked.Read(ref _compStoredBytes),
            CompressedRawBytes: System.Threading.Interlocked.Read(ref _compRawBytes),
            MaxBytes: _options.MaxBytes,
            TracesDays: _options.TracesDaysEffective,
            LogsDays: _options.LogsDaysEffective,
            MetricsDays: _options.MetricsDaysEffective,
            SweepMinutes: _options.RetentionSweepMinutes,
            RollupEnabled: _options.RollupEnabledEffective,
            RollupSeconds: _options.RollupResolutionSecondsEffective);
    }

    /// <summary>Verbrauch je Tabelle. Bevorzugt dbstat (echte Pages je Objekt,
    /// inkl. FTS-Shadow-Gruppen + „_sonstige"); sonst logische Datenmenge je
    /// Tabelle via SUM(LENGTH(...)) (Rows + Nutzdaten-Bytes, Full-Scan — die
    /// Depot-Seite ist eine Operator-Sicht, kein Hot-Pfad).</summary>
    private List<DepotTableStats> CollectTableStats(out bool dbStat)
    {
        // Versuch 1: dbstat (pgsize je Objekt).
        var perObject = new Dictionary<string, long>(System.StringComparer.Ordinal);
        try
        {
            using var rc = OpenReadConnection();
            using var cmd = new SqliteCommand(
                "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name ORDER BY name", rc);
            using var r = cmd.ExecuteReader();
            while (r.Read()) perObject[r.GetString(0)] = r.GetInt64(1);
            dbStat = true;
        }
        catch
        {
            dbStat = false;   // kein dbstat (gebündeltes e_sqlite3) → Schätzung
        }

        if (dbStat) return CollectDbStatTables(perObject);
        return CollectLogicalTables();
    }

    /// <summary>dbstat-Zweig: Basistabellen einzeln (mit Zeilenzahl), FTS-Shadow-
    /// Tabellen zu einem Eintrag je Signal zusammengefasst (Rows = -1 — eine Zeile
    /// pro Page, nicht sinnvoll), Rest (Indexe, sqlite_master, intern) zu
    /// „_sonstige".</summary>
    private List<DepotTableStats> CollectDbStatTables(Dictionary<string, long> perObject)
    {
        long ftsSpans = 0, ftsLogs = 0, sonstige = 0;
        foreach (var (name, bytes) in perObject)
        {
            if (name.StartsWith("heim_spans_fts", System.StringComparison.Ordinal)) { ftsSpans += bytes; continue; }
            if (name.StartsWith("heim_logs_fts", System.StringComparison.Ordinal)) { ftsLogs += bytes; continue; }
            // Basistabellen erscheinen unten einzeln — hier zählt nur, was KEINE
            // der bekannten Tabellen ist (Indexe tragen deren Namen nicht).
            switch (name)
            {
                case "heim_spans":
                case "heim_logs":
                case "heim_metrics":
                case "heim_metric_series":
                case "heim_metrics_rollup":
                case "heim_span_attrs":
                case "heim_log_attrs":
                    break;
                default: sonstige += bytes; break;
            }
        }

        var tables = new List<DepotTableStats>
        {
            new("heim_spans", Count("heim_spans"), Get("heim_spans")),
            new("heim_logs", Count("heim_logs"), Get("heim_logs")),
            new("heim_metrics", Count("heim_metrics"), Get("heim_metrics")),
            new("heim_metric_series", Count("heim_metric_series"), Get("heim_metric_series")),
            new("heim_metrics_rollup", Count("heim_metrics_rollup"), Get("heim_metrics_rollup")),
            new("heim_span_attrs", Count("heim_span_attrs"), Get("heim_span_attrs")),
            new("heim_log_attrs", Count("heim_log_attrs"), Get("heim_log_attrs")),
            new("heim_spans_fts*", -1, ftsSpans),
            new("heim_logs_fts*", -1, ftsLogs),
            new("_sonstige", -1, sonstige),
        };
        return tables;

        long Get(string table) => perObject.TryGetValue(table, out var b) ? b : 0;
    }

    /// <summary>Fallback ohne dbstat: logische Datenmenge je Tabelle
    /// (SUM(LENGTH()) aller Spalten) + Zeilenzahl — Full-Scan je Tabelle, in
    /// einer Read-Connection gesammelt. Index-/Page-Overhead ist nicht enthalten
    /// (der steckt in den exakten File/Used-PRAGMAs des DTO).</summary>
    private List<DepotTableStats> CollectLogicalTables()
    {
        var tables = new List<DepotTableStats>();
        using var rc = OpenReadConnection();

        Add("heim_spans", "LENGTH(trace_id)+LENGTH(span_id)+LENGTH(COALESCE(parent_id,''))+LENGTH(name)" +
             "+LENGTH(COALESCE(status_msg,''))+LENGTH(COALESCE(attrs_json,''))+LENGTH(COALESCE(events_json,''))" +
             "+LENGTH(COALESCE(links_json,''))+LENGTH(COALESCE(resource_json,''))" +
             "+LENGTH(COALESCE(scope_name,''))+LENGTH(COALESCE(scope_version,''))");
        Add("heim_logs", "LENGTH(COALESCE(trace_id,''))+LENGTH(COALESCE(span_id,''))" +
             "+LENGTH(COALESCE(severity_text,''))+LENGTH(COALESCE(body,''))+LENGTH(COALESCE(attrs_json,''))" +
             "+LENGTH(COALESCE(resource_json,''))+LENGTH(COALESCE(scope_name,''))+LENGTH(COALESCE(scope_version,''))");
        Add("heim_metrics", "LENGTH(name)+LENGTH(COALESCE(unit,''))" +
             "+LENGTH(COALESCE(bucket_counts_json,''))+LENGTH(COALESCE(explicit_bounds_json,''))" +
             "+LENGTH(COALESCE(attrs_json,''))+LENGTH(COALESCE(resource_json,''))" +
             "+LENGTH(COALESCE(scope_name,''))+LENGTH(COALESCE(scope_version,''))");
        Add("heim_metric_series", "LENGTH(name)+LENGTH(attrs_json)+LENGTH(resource_json)" +
             "+LENGTH(scope_name)+LENGTH(scope_version)");
        Add("heim_metrics_rollup", "LENGTH(name)+LENGTH(COALESCE(unit,''))" +
             "+LENGTH(COALESCE(bucket_counts_json,''))+LENGTH(COALESCE(explicit_bounds_json,''))" +
             "+LENGTH(COALESCE(attrs_json,''))+LENGTH(COALESCE(resource_json,''))" +
             "+LENGTH(COALESCE(scope_name,''))+LENGTH(COALESCE(scope_version,''))");
        Add("heim_span_attrs", "LENGTH(key)+LENGTH(value)");
        Add("heim_log_attrs", "LENGTH(key)+LENGTH(value)");
        return tables;

        void Add(string table, string dataLenExpr)
        {
            using var cmd = new SqliteCommand(
                $"SELECT COUNT(*), COALESCE(SUM({dataLenExpr}), 0) FROM {table}", rc);
            using var r = cmd.ExecuteReader();
            r.Read();
            tables.Add(new DepotTableStats(table, r.GetInt64(0), r.GetInt64(1)));
        }
    }

    /// <summary>Dateigröße lesen (0, wenn die Datei noch nicht existiert — frische DB).</summary>
    private static long FileSizeBytes(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }
        catch { return 0; }
    }
}