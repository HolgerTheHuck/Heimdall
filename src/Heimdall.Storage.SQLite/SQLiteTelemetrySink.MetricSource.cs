using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        sb.Append(" ORDER BY name");

        var names = new List<string>();
        lock (_gate) using (var cmd = Build(sb.ToString(), ps)) using (var r = cmd.ExecuteReader())
            while (r.Read()) names.Add(r.GetString(0));
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

        var sb = SqlBuilder();
        sb.Append("SELECT name, unit, type, temporality, ts_unix_nano, value, count, sum, min, max, " +
                  "bucket_counts_json, explicit_bounds_json, attrs_json, resource_json, scope_name " +
                  "FROM heim_metrics WHERE name IN (");
        var ps = new List<SqliteParameter>();
        for (int i = 0; i < query.Names.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var pname = "@n" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(pname);
            ps.Add(Param(pname, query.Names[i]));
        }
        sb.Append(')');
        if (query.FromUnixNano is not null) { sb.Append(" AND ts_unix_nano >= @from"); ps.Add(Param("@from", query.FromUnixNano.Value)); }
        if (query.ToUnixNano is not null) { sb.Append(" AND ts_unix_nano <= @to"); ps.Add(Param("@to", query.ToUnixNano.Value)); }
        sb.Append(" ORDER BY name, ts_unix_nano ASC LIMIT @lim");
        ps.Add(Param("@lim", Math.Max(1, query.Limit)));

        var list = new List<HMetricPointView>();
        lock (_gate) using (var cmd = Build(sb.ToString(), ps)) using (var r = cmd.ExecuteReader())
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
        sb.Append(" LIMIT @cap");
        ps.Add(Param("@cap", SourceScanCap));

        var rows = new List<(string?, string?)>();
        lock (_gate) using (var cmd = Build(sb.ToString(), ps)) using (var r = cmd.ExecuteReader())
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
        private static readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);
        public static bool IsMatch(string input, string pattern)
        {
            Regex r;
            lock (_cache)
            {
                if (!_cache.TryGetValue(pattern, out r!))
                {
                    r = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                    _cache[pattern] = r;
                }
            }
            return r.IsMatch(input);
        }
    }
}