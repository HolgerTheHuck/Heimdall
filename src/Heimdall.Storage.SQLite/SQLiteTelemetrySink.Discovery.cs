using System;
using System.Collections.Generic;
using System.Text;
using Heimdall;
using Microsoft.Data.Sqlite;

namespace Heimdall.Storage.SQLite;

// ---------------------------------------------------------------------------
// Service-/Version-Discovery (IHeimdallQuery-Default-Methoden, partial der
// Sink) fuer das Service-Dropdown der UI. Quelle sind die Attr-Index-Tabellen
// heim_log_attrs/heim_span_attrs (Trigger-expandiert aus attrs_json UND
// resource_json): DISTINCT value WHERE key=… laeuft als Range-Scan ueber
// idx_*_attrs_kv(key, value) — kein Cache noetig, die UI fragt einmal pro
// Seiten-Request. Key-Normalisierung service.name ↔ service_name wie in
// SearchLogs (Apps koennen die Unterstrich-Form als eigenes Attr schicken).
// ---------------------------------------------------------------------------

public sealed partial class SQLiteTelemetrySink
{
    public IReadOnlyList<string> ListServiceNames(long? fromUnixNano = null, long? toUnixNano = null)
    {
        // UNION (nicht UNION ALL): derselbe Service kann Logs UND Spans schicken.
        var sb = SqlBuilder();
        var ps = new List<SqliteParameter>();

        sb.Append("SELECT DISTINCT a.value FROM heim_log_attrs a");
        if (fromUnixNano is not null || toUnixNano is not null)
        {
            // Zeitraum: Rowid-Join auf die Basistabelle (O(1) pro Attr-Zeile).
            sb.Append(" JOIN heim_logs l ON l.rowid = a.log_rowid");
            if (fromUnixNano is not null) { sb.Append(" AND l.ts_unix_nano >= @lfrom"); ps.Add(Param("@lfrom", fromUnixNano.Value)); }
            if (toUnixNano is not null) { sb.Append(" AND l.ts_unix_nano <= @lto"); ps.Add(Param("@lto", toUnixNano.Value)); }
        }
        sb.Append(" WHERE a.key IN ('service.name','service_name')");

        sb.Append(" UNION ");

        sb.Append("SELECT DISTINCT a.value FROM heim_span_attrs a");
        if (fromUnixNano is not null || toUnixNano is not null)
        {
            sb.Append(" JOIN heim_spans s ON s.rowid = a.span_rowid");
            if (fromUnixNano is not null) { sb.Append(" AND s.start_unix_nano >= @sfrom"); ps.Add(Param("@sfrom", fromUnixNano.Value)); }
            if (toUnixNano is not null) { sb.Append(" AND s.start_unix_nano <= @sto"); ps.Add(Param("@sto", toUnixNano.Value)); }
        }
        sb.Append(" WHERE a.key IN ('service.name','service_name')");
        sb.Append(" ORDER BY 1");

        var names = new List<string>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    public IReadOnlyList<string> ListServiceVersions(string serviceName,
        long? fromUnixNano = null, long? toUnixNano = null)
    {
        if (string.IsNullOrEmpty(serviceName)) return Array.Empty<string>();

        // Paar-Semantik: service.name und service.version muessen auf derselben
        // Log-/Span-Zeile sitzen (JOIN beider Attr-Zeilen ueber die rowid). Die
        // s-Seite laeuft ueber idx_*_attrs_kv, die v-Seite pro Probe ueber den
        // PK-Prefix (rowid) — bewusst NICHT resource_json ->> (Full-Scan mit
        // JSON-Parse pro Zeile ueber die groesste Tabelle).
        var sb = SqlBuilder();
        var ps = new List<SqliteParameter>();

        sb.Append("SELECT DISTINCT v.value FROM heim_log_attrs v " +
                  "JOIN heim_log_attrs s ON s.log_rowid = v.log_rowid");
        AppendServiceVersionPredicates(sb, ps, serviceName, suffix: "", baseTable: "heim_logs",
            rowIdCol: "log_rowid", tsCol: "ts_unix_nano", fromUnixNano, toUnixNano);

        sb.Append(" UNION ");

        sb.Append("SELECT DISTINCT v.value FROM heim_span_attrs v " +
                  "JOIN heim_span_attrs s ON s.span_rowid = v.span_rowid");
        AppendServiceVersionPredicates(sb, ps, serviceName, suffix: "2", baseTable: "heim_spans",
            rowIdCol: "span_rowid", tsCol: "start_unix_nano", fromUnixNano, toUnixNano);

        sb.Append(" ORDER BY 1");

        var versions = new List<string>();
        using (var rc = OpenReadConnection())
        using (var cmd = BuildRead(rc, sb.ToString(), ps))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) versions.Add(r.GetString(0));
        return versions;
    }

    /// <summary>WHERE-Praedikate eines Versions-Discovery-Zweigs: s = die
    /// service.name-Zeile (Index-Zugriff auf (key,value)), v = die
    /// service.version-Zeile desselben Records; optionaler Zeitraum-Subselect
    /// ueber die Basistabelle. <paramref name="suffix"/> haelt die Parameter-
    /// Namen der beiden UNION-Zweige disjunkt (@svc/@svc2, …).</summary>
    private void AppendServiceVersionPredicates(StringBuilder sb, List<SqliteParameter> ps,
        string serviceName, string suffix, string baseTable, string rowIdCol, string tsCol,
        long? fromUnixNano, long? toUnixNano)
    {
        sb.Append(" WHERE s.key IN ('service.name','service_name') AND s.value = @svc").Append(suffix)
          .Append(" AND v.key IN ('service.version','service_version')");
        ps.Add(Param("@svc" + suffix, serviceName));
        if (fromUnixNano is not null || toUnixNano is not null)
        {
            sb.Append(" AND s.").Append(rowIdCol)
              .Append(" IN (SELECT rowid FROM ").Append(baseTable).Append(" WHERE 1=1");
            if (fromUnixNano is not null)
            {
                sb.Append(" AND ").Append(tsCol).Append(" >= @from").Append(suffix);
                ps.Add(Param("@from" + suffix, fromUnixNano.Value));
            }
            if (toUnixNano is not null)
            {
                sb.Append(" AND ").Append(tsCol).Append(" <= @to").Append(suffix);
                ps.Add(Param("@to" + suffix, toUnixNano.Value));
            }
            sb.Append(')');
        }
    }
}