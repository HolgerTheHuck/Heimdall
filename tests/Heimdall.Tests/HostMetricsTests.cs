using System;
using System.IO;
using System.Linq;
using System.Threading;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// C3 — Host-Self-Observability: die vom Sink synthetisierten heimdall.host.*-Metriken
/// (in-memory, nicht in heim_metrics gespeichert) erscheinen in ListMetricNames und
/// FetchPoints. Ingest-Counter (Sum/Cumulative, signal-Label) zählen persistierte Items
/// pro Signal; Sweep-Latenz (Gauge, Sekunden) den letzten realen Sweep. Spiegelt
/// <see cref="RetentionMetricsTests"/> (A4-Muster).
/// </summary>
public class HostMetricsTests
{
    private static long DaysAgoNs(int d) => DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds() * 1_000_000_000L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-hostmet-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private static SQLiteTelemetrySink NewSink(string path) =>
        new(new SQLiteTelemetryOptions
        { DataPath = path, RetentionDays = 0, WalMode = false, AutoVacuum = true, RetentionSweepMinutes = 0 });

    private static int _spanSeq;
    private static HSpan Span(long startNs)
    {
        var sid = new byte[8];
        int seq = Interlocked.Increment(ref _spanSeq);
        sid[0] = (byte)(seq >> 8); sid[7] = (byte)seq;
        return new(new byte[16], sid, null, "s", HSpanKind.Server,
            startNs, startNs + 1_000_000, HStatusCode.Ok, null,
            Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            new HResource(Array.Empty<HAttribute>()), null);
    }

    private static HLogRecord Log(long tsNs) =>
        new(tsNs, HSeverity.Info, "INFO", "b", null, null, Array.Empty<HAttribute>(), null, null);

    private static HMetricPoint Metric(long tsNs, double value) =>
        new("host-test-metric", null, HMetricType.Gauge, HTemporality.Unspecified,
            tsNs, value, null, null, null, null, null, null,
            Array.Empty<HAttribute>(), null, null);

    [Fact]
    public void ListMetricNames_Enthaelt_Host_SelfObs_Metriken()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var names = sink.ListMetricNames();
            Assert.Contains("heimdall.host.ingest", names);
            Assert.Contains("heimdall.host.sweep.duration", names);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Ingest_Counter_Zaehlt_Persistierte_Items_Pro_Signal()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[] { Span(DaysAgoNs(0)), Span(DaysAgoNs(0) + 1), Span(DaysAgoNs(0) + 2) });
            sink.WriteLogs(new[] { Log(DaysAgoNs(0)), Log(DaysAgoNs(0) + 1) });
            sink.WriteMetrics(new[] { Metric(DaysAgoNs(0), 1), Metric(DaysAgoNs(0) + 1, 2),
                                      Metric(DaysAgoNs(0) + 2, 3), Metric(DaysAgoNs(0) + 3, 4) });

            var pts = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.host.ingest" }, null, null, null, 100));
            var bySignal = pts.ToDictionary(p => p.Labels["signal"], p => (long)p.Value);

            Assert.Equal(3, bySignal["spans"]);
            Assert.Equal(2, bySignal["logs"]);
            Assert.Equal(4, bySignal["metrics"]);
            Assert.All(pts, p => Assert.Equal(HMetricType.Sum, p.Type));
            Assert.All(pts, p => Assert.Equal(HTemporality.Cumulative, p.Temporality));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Ingest_Matcher_Signal_Logs_Filtert_Auf_Einen_Punkt()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[] { Span(DaysAgoNs(0)) });
            sink.WriteLogs(new[] { Log(DaysAgoNs(0)), Log(DaysAgoNs(0) + 1) });

            var pts = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.host.ingest" },
                new[] { new HLabelMatcher("signal", "logs", HMatchOp.Eq) },
                null, null, 100));
            var pt = Assert.Single(pts);
            Assert.Equal("logs", pt.Labels["signal"]);
            Assert.Equal(2, (long)pt.Value);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Sweep_Duration_Gauge_Nach_Realem_Sweep_Groesser_oder_Gleich_Null()
    {
        var path = NewDbPath();
        try
        {
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = path, RetentionDays = 3, WalMode = false, AutoVacuum = true, RetentionSweepMinutes = 0 });

            // Ein frischer Span (bleibt) — der Sweep läuft dennoch (Retention aktiv),
            // misst also eine echte Dauer und trägt sie in _hostSweepDurationTicks ein.
            sink.WriteSpans(new[] { Span(DaysAgoNs(0)) });
            sink.SweepRetention();

            var pts = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.host.sweep.duration" }, null, null, null, 100));
            var pt = Assert.Single(pts);
            Assert.Equal(HMetricType.Gauge, pt.Type);
            Assert.True(pt.Value >= 0);
            Assert.False(pt.Labels.ContainsKey("signal"));   // kein signal-Label
        }
        finally { Cleanup(path); }
    }
}