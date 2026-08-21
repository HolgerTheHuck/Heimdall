using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Workstream F — Metriken-Downsampling (Rollup): rohe Metrik-Punkte aelter als
/// RawDays werden zu ResolutionSeconds-Buckets aggregiert (statt hart geloescht).
/// Triggert den Sweep direkt (SweepRetention/RollupRawMetrics sind internal via
/// InternalsVisibleTo); prueft Aggregation pro Type, Disjointness, Query-Paritaet,
/// Discovery-Paritaet, Idempotenz, Cap-Eviction und den Disabled-Pfad (== heute).
/// </summary>
public class RollupTests
{
    private static long DaysAgoNs(int d) => DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds() * 1_000_000_000L;
    private static long NowNs => DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1_000_000_000L;
    private const long Sec = 1_000_000_000L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-roll-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    // Rollup-faehiger Sink: RawDays=1, MetricsDays=30 (Raw<Metrics), Timer aus.
    private static SQLiteTelemetrySink NewSink(string path, Action<SQLiteTelemetryOptions>? tune = null)
    {
        var o = new SQLiteTelemetryOptions
        {
            DataPath = path, RetentionDays = 0, WalMode = false,
            AutoVacuum = true, RetentionSweepMinutes = 0,
            Retention = new HeimdallRetentionOptions { MetricsDays = 30 },
            Rollup = new HeimdallRollupOptions { Enabled = true, ResolutionSeconds = 60, RawDays = 1 }
        };
        tune?.Invoke(o);
        return new SQLiteTelemetrySink(o);
    }

    private static HResource Res(string svc) => new(new[] { new HAttribute("service.name", svc) });

    private static HMetricPoint Gauge(long ts, double v, string name = "g") =>
        new(name, null, HMetricType.Gauge, HTemporality.Unspecified, ts, v, null, null, null, null, null, null,
            new[] { new HAttribute("k", "v") }, Res("svc"), null);

    private static HMetricPoint SumDelta(long ts, double v, string name = "sd") =>
        new(name, null, HMetricType.Sum, HTemporality.Delta, ts, v, null, null, null, null, null, null,
            new[] { new HAttribute("k", "v") }, Res("svc"), null);

    private static HMetricPoint SumCum(long ts, double v, string name = "sc") =>
        new(name, null, HMetricType.Sum, HTemporality.Cumulative, ts, v, null, null, null, null, null, null,
            new[] { new HAttribute("k", "v") }, Res("svc"), null);

    // Histogramm-Punkt: Value == Sum (overloaded), Count, Sum, Min, Max, per-Bucket-Counts.
    private static HMetricPoint Hist(long ts, HTemporality temp, double sum, long count, double min, double max,
        long[] buckets, string name = "h") =>
        new(name, null, HMetricType.Histogram, temp, ts, sum, count, sum, min, max,
            buckets, new double[] { 0, 5, double.PositiveInfinity },
            new[] { new HAttribute("k", "v") }, Res("svc"), null);

    private static HMetricPoint OldMetric(long ts, string name = "capm") =>
        new(name, null, HMetricType.Sum, HTemporality.Cumulative, ts, 1, 1, null, null, null, null, null,
            Array.Empty<HAttribute>(), Res("svc"), null);

    private static List<HMetricPointView> Fetch(SQLiteTelemetrySink s, string name, long? from = null, long? to = null) =>
        s.FetchPoints(new HMetricQuery(new[] { name }, null, from, to, 20000)).ToList();

    // -----------------------------------------------------------------------
    // Validierung
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Validation_Wirft_Bei_Ungueltigen_Werten()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = NewDbPath(), RetentionSweepMinutes = 0,
              Rollup = new HeimdallRollupOptions { Enabled = true, ResolutionSeconds = 0 } }));

        Assert.Throws<InvalidOperationException>(() =>
            new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = NewDbPath(), RetentionSweepMinutes = 0,
              Rollup = new HeimdallRollupOptions { Enabled = true, RawDays = -1 } }));

        // RawDays > MetricsDaysEffective bei Enabled → leeres Rollup-Fenster.
        Assert.Throws<InvalidOperationException>(() =>
            new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = NewDbPath(), RetentionSweepMinutes = 0,
              Retention = new HeimdallRetentionOptions { MetricsDays = 5 },
              Rollup = new HeimdallRollupOptions { Enabled = true, RawDays = 10 } }));
    }

    // -----------------------------------------------------------------------
    // Aggregation pro Type (ein Bucket, mehrere Raw-Punkte ts-aufsteigend)
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Aggregation_Pro_Type()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            long b = DaysAgoNs(2);   // gemeinsamer 60s-Bucket (Offsets < 1s)
            // Gauge: LAST value by ts → 30.
            sink.WriteMetrics(new[] { Gauge(b, 10), Gauge(b + 1, 20), Gauge(b + 2, 30) });
            // Sum/Delta: SUM value → 60.
            sink.WriteMetrics(new[] { SumDelta(b, 10), SumDelta(b + 1, 20), SumDelta(b + 2, 30) });
            // Sum/Cumulative: LAST value by ts → 30.
            sink.WriteMetrics(new[] { SumCum(b, 10), SumCum(b + 1, 20), SumCum(b + 2, 30) });
            // Hist/Delta: elementweise SUM buckets, SUM sum, SUM count, MIN min, MAX max.
            sink.WriteMetrics(new[]
            {
                Hist(b, HTemporality.Delta, 100, 6, 1, 10, new long[] { 1, 2, 3 }),
                Hist(b + 1, HTemporality.Delta, 200, 15, 2, 20, new long[] { 4, 5, 6 })
            });
            // Hist/Cumulative: LAST by ts (B/S/C des zweiten), MIN min, MAX max.
            sink.WriteMetrics(new[]
            {
                Hist(b, HTemporality.Cumulative, 100, 6, 1, 10, new long[] { 1, 2, 3 }),
                Hist(b + 1, HTemporality.Cumulative, 200, 15, 2, 20, new long[] { 4, 5, 6 })
            });

            sink.SweepRetention();

            Assert.Equal(0, sink.CountMetrics());        // alle Raw eingrollt
            Assert.Equal(5, sink.CountMetricsRollup());  // 5 Typen × 1 Bucket

            var g = Fetch(sink, "g").Single();
            Assert.Equal(30, g.Value);                   // Gauge LAST

            var sd = Fetch(sink, "sd").Single();
            Assert.Equal(60, sd.Value);                  // Sum/Delta SUM
            Assert.Equal(HTemporality.Delta, sd.Temporality);

            var sc = Fetch(sink, "sc").Single();
            Assert.Equal(30, sc.Value);                  // Sum/Cumulative LAST
            Assert.Equal(HTemporality.Cumulative, sc.Temporality);

            var hd = Fetch(sink, "h").Where(p => p.Temporality == HTemporality.Delta).Single();
            Assert.Equal(300, hd.Sum);                   // 100 + 200
            Assert.Equal(21, hd.Count);                  // 6 + 15
            Assert.Equal(1, hd.Min);                     // MIN
            Assert.Equal(20, hd.Max);                    // MAX
            Assert.Equal(new long[] { 5, 7, 9 }, hd.BucketCounts);   // elementweise SUM

            var hc = Fetch(sink, "h").Where(p => p.Temporality == HTemporality.Cumulative).Single();
            Assert.Equal(200, hc.Sum);                   // LAST sum
            Assert.Equal(15, hc.Count);                  // LAST count
            Assert.Equal(new long[] { 4, 5, 6 }, hc.BucketCounts);   // LAST buckets
            Assert.Equal(1, hc.Min);
            Assert.Equal(20, hc.Max);
        }
        finally { Cleanup(path); }
    }

    // -----------------------------------------------------------------------
    // Disjointness / kein Doppelzaehlen
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Disjoint_Kein_Doppelzaehlen()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            long b = DaysAgoNs(2);
            // Zwei distincte 60s-Buckets (120s auseinander), je ein Punkt.
            sink.WriteMetrics(new[] { SumCum(b, 5, "dj"), SumCum(b + 120 * Sec, 7, "dj") });

            sink.SweepRetention();

            Assert.Equal(0, sink.CountMetrics());
            Assert.Equal(2, sink.CountMetricsRollup());

            var pts = Fetch(sink, "dj").OrderBy(p => p.TimeUnixNano).ToList();
            Assert.Equal(2, pts.Count);                 // jedes logische Sample genau einmal
            Assert.Equal(5, pts[0].Value);
            Assert.Equal(7, pts[1].Value);
            Assert.True(pts[1].TimeUnixNano - pts[0].TimeUnixNano >= 120 * Sec);
        }
        finally { Cleanup(path); }
    }

    // -----------------------------------------------------------------------
    // Query-Paritaet: Raw-Fenster vs Roll-Fenster
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Query_Parietaet_Raw_Roll()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteMetrics(new[] { SumCum(NowNs, 42, "rq") });       // neu → bleibt Raw
            sink.WriteMetrics(new[] { SumCum(DaysAgoNs(2), 99, "rq") }); // alt → rollt

            sink.SweepRetention();

            // (a) Fenster [now-1s, now] → nur Raw (Rollup bei 2d < from).
            var raw = Fetch(sink, "rq", NowNs - Sec, NowNs + Sec);
            Assert.Single(raw);
            Assert.Equal(42, raw[0].Value);

            // (b) Fenster [0, now-1d] → nur Rollup (Raw bei now > to).
            var roll = Fetch(sink, "rq", 0, DaysAgoNs(1));
            Assert.Single(roll);
            Assert.Equal(99, roll[0].Value);
        }
        finally { Cleanup(path); }
    }

    // -----------------------------------------------------------------------
    // Discovery-Paritaet nach voller Alterung der Raw
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Discovery_Parietaet_Nach_Alterung()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            // Einzigartiger Name + Resource-Attr, alt → rollt; Raw danach weg.
            sink.WriteMetrics(new[]
            {
                new HMetricPoint("disc.metric", null, HMetricType.Sum, HTemporality.Cumulative,
                    DaysAgoNs(2), 1, 1, null, null, null, null, null,
                    Array.Empty<HAttribute>(), Res("discsvc"), null)
            });

            sink.SweepRetention();

            Assert.Equal(0, sink.CountMetrics());
            Assert.Equal(1, sink.CountMetricsRollup());

            Assert.Contains("disc.metric", sink.ListMetricNames());
            Assert.Contains("service.name", sink.ListLabelNames());
            Assert.Contains("discsvc", sink.ListLabelValues("service.name"));
        }
        finally { Cleanup(path); }
    }

    // -----------------------------------------------------------------------
    // Sweep-Idempotenz
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Sweep_Idempotenz()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            long b = DaysAgoNs(2);
            sink.WriteMetrics(new[] { SumCum(b, 5, "id"), SumCum(b + 120 * Sec, 7, "id") });

            sink.SweepRetention();
            var after1 = Fetch(sink, "id").OrderBy(p => p.TimeUnixNano).ToList();
            long rows1 = sink.CountMetricsRollup();

            sink.SweepRetention();   // zweiter Sweep — nichts mehr zu rollen
            var after2 = Fetch(sink, "id").OrderBy(p => p.TimeUnixNano).ToList();
            long rows2 = sink.CountMetricsRollup();

            Assert.Equal(rows1, rows2);
            Assert.Equal(after1.Count, after2.Count);
            Assert.True(after1.Zip(after2).All(p => p.First.TimeUnixNano == p.Second.TimeUnixNano
                                                  && p.First.Value == p.Second.Value));
        }
        finally { Cleanup(path); }
    }

    // -----------------------------------------------------------------------
    // Cap-Eviction evictet Rollup-Zeilen
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Cap_Eviction_Evictet_Rollup()
    {
        const int N = 3000;   // > Eviction-Tranche (1000), damit partiell evictet wird.
        // 1. Auf separater DB Overhead + Rollup-Belegung messen (MaxBytes nur im Ctor).
        var op = NewDbPath();
        long overhead, uRoll;
        using (var os = NewSink(op))
        {
            overhead = os.UsedBytes();
            os.WriteMetrics(OldMetrics(N));
            os.RollupRawMetrics();             // rollt (ohne Vacuum) → Eviction-Zeitpunkt-View
            uRoll = os.UsedBytes();
        }
        Cleanup(op);
        Assert.True(uRoll > overhead);

        // 2. MaxBytes auf ~80 % des Rollup-Anteils → erzwingt Eviction von Rollup-Zeilen.
        long maxBytes = overhead + (uRoll - overhead) * 4 / 5;
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o => o.MaxBytes = maxBytes);
            sink.WriteMetrics(OldMetrics(N));
            sink.SweepRetention();             // rollt, dann Cap-Eviction ueber Rollup-Zeilen

            Assert.True(sink.UsedBytes() <= maxBytes,
                $"UsedBytes {sink.UsedBytes()} > MaxBytes {maxBytes}");
            Assert.True(sink.CountMetricsRollup() < N,   // evictet
                $"Rollup-Zeilen {sink.CountMetricsRollup()} >= N {N}");
            Assert.True(sink.CountMetricsRollup() > 0);  // nicht alles weg

            // Eviction-Zaehler (metrics, gefaltet mit metrics_rollup) > 0.
            var ev = sink.FetchPoints(new HMetricQuery(new[] { "heimdall.retention.evicted" }, null, null, null, 100))
                .Single(p => p.Labels.TryGetValue("signal", out var s) && s == "metrics");
            Assert.True(ev.Value > 0);
        }
        finally { Cleanup(path); }
    }

    private static List<HMetricPoint> OldMetrics(int n)
    {
        var list = new List<HMetricPoint>(n);
        long base0 = DaysAgoNs(10);                  // weit genug in der Vergangenheit, dass
        for (int i = 0; i < n; i++)                  // alle N Punkte (je 120s auseinander) < RawDays
            list.Add(OldMetric(base0 + i * 120 * Sec));   // bleiben und < MetricsDays (kein TTL-Delete).
        return list;
    }

    // -----------------------------------------------------------------------
    // Disabled == heute (Regression-Sicherheit)
    // -----------------------------------------------------------------------

    [Fact]
    public void Rollup_Deaktiviert_Ist_Heute()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o =>
            {
                o.Rollup = new HeimdallRollupOptions { Enabled = false };  // explizit aus
                o.Retention = new HeimdallRetentionOptions { MetricsDays = 30 };
            });
            sink.WriteMetrics(new[] { OldMetric(DaysAgoNs(40)), OldMetric(DaysAgoNs(1)) });

            sink.SweepRetention();   // nur TTL-Delete (40d > 30d), kein Rollup

            Assert.Equal(1, sink.CountMetrics());       // nur der 1d-Punkt bleibt
            Assert.Equal(0, sink.CountMetricsRollup()); // nie gerollt
        }
        finally { Cleanup(path); }
    }
}