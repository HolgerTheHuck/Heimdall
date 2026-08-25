using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Heimdall;
using Microsoft.Data.Sqlite;

namespace Heimdall.Storage.SQLite;

// ---------------------------------------------------------------------------
// Workstream F — Metriken-Downsampling (Rollup), partial der Sink.
// Rohe Metrik-Punkte aelter als RawDays werden zu ResolutionSeconds-Buckets
// aggregiert (statt hart geloescht) und bis MetricsDaysEffective gehalten.
// Aufgerufen in SweepRetention VOR dem TTL-Delete. Alles in C# aggregiert
// (Histogramm-Bucket-Merge + +Inf-Normalisierung ist in SQL fragil; Sweep ist
// nicht hot-path). Disjointness konstruktiv: eine Rollup-Zeile entsteht nur,
// wenn ihre Raw-Zeilen im selben Tx geloescht wurden -> kein Query-Boundary-
// Filter noetig (siehe Plan). Opt-In (Enabled=false = heute).
// ---------------------------------------------------------------------------

public sealed partial class SQLiteTelemetrySink
{
    private const int RollupBatchSize = 5000;

    // Rohe Metrik-Punkte aelter als RawDays zu Buckets aggregieren. Liefert true,
    // falls Raw-Zeilen eingrollt wurden (-> anyDeleted -> incremental_vacuum
    // gibt die Raw-Pages frei). Gate: RollupEnabled && MetricsDaysEffective > 0.
    internal bool RollupRawMetrics()
    {
        if (!_options.RollupEnabledEffective || _options.MetricsDaysEffective <= 0) return false;

        long res = _options.RollupResolutionSecondsEffective * 1_000_000_000L;   // ns pro Bucket
        long rawCutoff = DateTimeOffset.UtcNow
            .AddDays(-_options.RollupRawDaysEffective)
            .ToUnixTimeSeconds() * 1_000_000_000L;
        // Letzter bucket_start, dessen Bucket endet <= rawCutoff — nur VOLLSTAENDIGE
        // Buckets rollen: ts < boundary => Bucket-Ende = start+res <= boundary <=
        // rawCutoff <= now (start = floor(ts/res)*res <= boundary-res).
        long boundary = (rawCutoff / res) * res;
        if (boundary <= 0) return false;

        bool anyRolled = false;
        while (true)
        {
            var rows = ReadRollupBatch(boundary);
            if (rows.Count == 0) break;

            // Bei vollem Batch koennte LIMIT mitten in der letzten (name, bucket)-
            // Gruppe geschnitten haben. Diese unvollstaendige Gruppe bleibt Raw und
            // rollt beim naechsten Sweep (ihre ts < boundary bleiben true).
            bool fullBatch = rows.Count >= RollupBatchSize;
            var lastRow = rows[rows.Count - 1];
            long lastBucket = (lastRow.Ts / res) * res;
            string lastName = lastRow.Name;

            // Nach (Fingerprint, bucket) gruppieren und in C# aggregieren.
            var groups = new Dictionary<RollupKey, (RollupAcc Acc, List<long> Rowids)>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                long bucket = (row.Ts / res) * res;
                if (fullBatch && row.Name == lastName && bucket == lastBucket)
                    continue;   // unvollstaendige Gruppe ueberspringen
                var key = new RollupKey(row.SeriesId, row.Name, row.Type, row.Temporality, row.Unit, bucket);
                if (!groups.TryGetValue(key, out var g))
                {
                    g = (new RollupAcc(), new List<long>());
                    groups[key] = g;
                }
                g.Acc.Add(row);
                g.Rowids.Add(row.Rowid);
            }

            // Nur vollstaendige, homogene Gruppen committen (gemischte Type/
            // Temporality defensiv skippen — deren Raw-Zeilen bleiben, altern
            // unter MetricsDaysEffective aus). Delete-Liste nur aus committed Gruppen.
            var committed = groups.Where(kv => !kv.Value.Acc.IsMixed).ToList();
            if (committed.Count == 0)
            {
                // Nur die unvollstaendige/ gemischte Gruppe drin — kein Fortschritt.
                if (!fullBatch) break;
                continue;
            }
            var rowids = new List<long>(committed.Sum(kv => kv.Value.Rowids.Count));
            foreach (var kv in committed) rowids.AddRange(kv.Value.Rowids);

            WriteRollupBatch(committed, rowids);
            anyRolled = true;

            if (!fullBatch) break;   // weniger als Batch-Limit => nichts mehr da
        }
        return anyRolled;
    }

    private List<RollupRow> ReadRollupBatch(long boundary)
    {
        // Hebel 4: series_id liegt direkt auf heim_metrics (kein Join noetig) —
        // attrs/resource/scope werden fuer den Rollup nicht mehr gelesen, die
        // Rollup-Zeile referenziert die Serie per series_id.
        const string sql =
            "SELECT rowid, name, unit, type, temporality, ts_unix_nano, value, count, sum, min, max, " +
            "bucket_counts_json, explicit_bounds_json, series_id " +
            "FROM heim_metrics WHERE ts_unix_nano < @b " +
            "ORDER BY name, ts_unix_nano ASC LIMIT @lim";
        var rows = new List<RollupRow>();
        lock (_gate)
        {
            using var cmd = new SqliteCommand(sql, _conn);
            cmd.Parameters.AddWithValue("@b", boundary);
            cmd.Parameters.AddWithValue("@lim", RollupBatchSize);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new RollupRow(
                    r.GetInt64(0), r.IsDBNull(13) ? 0 : r.GetInt64(13), r.GetString(1), NStr(r, 2),
                    (HMetricType)r.GetInt32(3), (HTemporality)r.GetInt32(4),
                    r.GetInt64(5), r.GetDouble(6),
                    NLong(r, 7), NDouble(r, 8), NDouble(r, 9), NDouble(r, 10),
                    ParseLongs(NStr(r, 11)), ParseDoubles(NStr(r, 12))));
            }
        }
        return rows;
    }

    // Ein Tx pro Batch: Rollup-Zeilen INSERT + Raw-Zeilen DELETE (idempotent —
    // nach Commit sind die Raw-Zeilen weg, kein Doppel-Roll bei Re-Sweep).
    private void WriteRollupBatch(
        List<KeyValuePair<RollupKey, (RollupAcc Acc, List<long> Rowids)>> committed,
        List<long> rowids)
    {
        // Hebel 4: series_id direkt aus der Quell-Zeile (kein Re-Resolve); attrs/
        // resource/scope liegen in heim_metric_series, nicht in der Rollup-Zeile.
        const string insertSql =
            "INSERT INTO heim_metrics_rollup (name, unit, type, temporality, bucket_start, resolution_seconds, " +
            "value, count, sum, min, max, bucket_counts_json, explicit_bounds_json, " +
            "attrs_json, resource_json, scope_name, scope_version, series_id) " +
            "VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17)";
        var idList = string.Join(',', rowids.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        int resSec = _options.RollupResolutionSecondsEffective;
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var kv in committed)
            {
                var k = kv.Key;
                var a = kv.Value.Acc;
                using var cmd = new SqliteCommand(insertSql, _conn, tx);
                cmd.Parameters.AddWithValue("@p0", k.Name);
                cmd.Parameters.AddWithValue("@p1", (object?)k.Unit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p2", (int)k.Type);
                cmd.Parameters.AddWithValue("@p3", (int)k.Temporality);
                cmd.Parameters.AddWithValue("@p4", k.BucketStart);
                cmd.Parameters.AddWithValue("@p5", resSec);
                cmd.Parameters.AddWithValue("@p6", a.Value);
                cmd.Parameters.AddWithValue("@p7", (object?)a.Count ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p8", (object?)a.Sum ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p9", (object?)a.Min ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p10", (object?)a.Max ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p11", a.BucketCountsJson ?? "[]");
                cmd.Parameters.AddWithValue("@p12", a.ExplicitBoundsJson ?? "[]");
                cmd.Parameters.AddWithValue("@p13", DBNull.Value);   // attrs_json — in heim_metric_series
                cmd.Parameters.AddWithValue("@p14", DBNull.Value);   // resource_json — in heim_metric_series
                cmd.Parameters.AddWithValue("@p15", DBNull.Value);   // scope_name — in heim_metric_series
                cmd.Parameters.AddWithValue("@p16", DBNull.Value);   // scope_version — in heim_metric_series
                cmd.Parameters.AddWithValue("@p17", k.SeriesId);
                cmd.ExecuteNonQuery();
            }
            using var del = new SqliteCommand(
                $"DELETE FROM heim_metrics WHERE rowid IN ({idList})", _conn, tx);
            del.ExecuteNonQuery();
            tx.Commit();
        }
    }

    // --- Hilfstypen -------------------------------------------------------

    // Hebel 4: Gruppierung ueber series_id (Fingerprint der Serie) statt der
    // Label-JSONs — die Serie ist durch series_id eindeutig identifiziert.
    private readonly record struct RollupKey(
        long SeriesId, string Name, HMetricType Type, HTemporality Temporality, string? Unit,
        long BucketStart);

    private sealed class RollupRow
    {
        public long Rowid;
        public long SeriesId;
        public string Name;
        public string? Unit;
        public HMetricType Type;
        public HTemporality Temporality;
        public long Ts;
        public double Value;
        public long? Count;
        public double? Sum, Min, Max;
        public IReadOnlyList<long>? BucketCounts;
        public IReadOnlyList<double>? ExplicitBounds;

        public RollupRow(long rowid, long seriesId, string name, string? unit, HMetricType type, HTemporality temp,
            long ts, double value, long? count, double? sum, double? min, double? max,
            IReadOnlyList<long>? bucketCounts, IReadOnlyList<double>? explicitBounds)
        {
            Rowid = rowid; SeriesId = seriesId; Name = name; Unit = unit; Type = type; Temporality = temp;
            Ts = ts; Value = value; Count = count; Sum = sum; Min = min; Max = max;
            BucketCounts = bucketCounts; ExplicitBounds = explicitBounds;
        }
    }

    // Aggregation pro (Fingerprint, bucket). Rows kommen ts-aufsteigend (ORDER BY
    // name, ts), darum ist "LAST by ts" = zuletzt hinzugefuegte Zeile der Gruppe.
    private sealed class RollupAcc
    {
        private HMetricType _type = (HMetricType)(-1);
        private HTemporality _temp = (HTemporality)(-1);
        private bool _first = true;
        private long _lastTs = long.MinValue;
        private double _lastValue;
        private IReadOnlyList<long>? _lastBuckets;
        private double _lastSum;
        private long _lastCount;

        public double Value;
        public long? Count;
        public double? Sum, Min, Max;
        public IReadOnlyList<long>? BucketCounts;
        public IReadOnlyList<double>? ExplicitBounds;
        public bool IsMixed;

        public void Add(RollupRow r)
        {
            if (_first) { _type = r.Type; _temp = r.Temporality; _first = false; }
            else if (r.Type != _type || r.Temporality != _temp) IsMixed = true;

            // min/max fuer Histogramm (und harmlos fuer andere, die keine tragen).
            if (r.Min.HasValue) Min = Min.HasValue ? Math.Min(Min.Value, r.Min.Value) : r.Min.Value;
            if (r.Max.HasValue) Max = Max.HasValue ? Math.Max(Max.Value, r.Max.Value) : r.Max.Value;

            switch (_type)
            {
                case HMetricType.Gauge:
                    // LAST value by ts.
                    if (r.Ts >= _lastTs) { _lastTs = r.Ts; _lastValue = r.Value; }
                    Value = _lastValue;
                    break;

                case HMetricType.Sum:
                    if (_temp == HTemporality.Delta)
                        Value += r.Value;               // SUM value (Delta kumuliert sich zur Bucket-Summe)
                    else
                    {                                    // Cumulative: LAST value by ts (Pass-through)
                        if (r.Ts >= _lastTs) { _lastTs = r.Ts; _lastValue = r.Value; }
                        Value = _lastValue;
                    }
                    break;

                case HMetricType.Histogram:
                    ExplicitBounds ??= r.ExplicitBounds;  // Grenzen sind konstant uber die Serie
                    if (_temp == HTemporality.Delta)
                    {
                        // elementweise SUM bucket_counts, SUM sum, SUM count.
                        BucketCounts = SumBuckets(BucketCounts, r.BucketCounts);
                        Sum = (Sum ?? 0) + (r.Sum ?? 0);
                        Count = (Count ?? 0) + (r.Count ?? 0);
                        Value = Sum ?? 0;                 // value-Spalte = Hist-Summe (overloaded)
                    }
                    else
                    {                                    // Cumulative: LAST by ts
                        if (r.Ts >= _lastTs)
                        {
                            _lastTs = r.Ts;
                            _lastBuckets = r.BucketCounts;
                            _lastSum = r.Sum ?? 0;
                            _lastCount = r.Count ?? 0;
                        }
                        BucketCounts = _lastBuckets;
                        Sum = _lastSum;
                        Count = _lastCount;
                        Value = _lastSum;
                    }
                    break;
            }
        }

        public string? BucketCountsJson =>
            BucketCounts is null ? null : HeimdallJson.WriteLongs(BucketCounts);
        public string? ExplicitBoundsJson =>
            ExplicitBounds is null ? null : HeimdallJson.WriteDoubles(ExplicitBounds);

        private static IReadOnlyList<long>? SumBuckets(IReadOnlyList<long>? acc, IReadOnlyList<long>? add)
        {
            if (add is null) return acc;
            if (acc is null || acc.Count == 0) return add;
            if (acc.Count != add.Count) return acc;   // Bounds-Mismatch — defensiv keep acc
            var r = new long[acc.Count];
            for (int i = 0; i < acc.Count; i++) r[i] = acc[i] + add[i];
            return r;
        }
    }
}