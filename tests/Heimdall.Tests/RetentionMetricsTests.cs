using System;
using System.IO;
using System.Linq;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// A4 — Retention- &amp; Eviction-Observability: die vom Sink synthetisierten
/// heimdall.*-Metriken (nicht in heim_metrics gespeichert) erscheinen in
/// ListMetricNames und FetchPoints. Counter: retention.deleted/evicted (Sum,
/// signal-Label); Gauges: storage.bytes, storage.rows (signal-Label).
/// </summary>
public class RetentionMetricsTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long DaysAgoNs(int d) => DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds() * 1_000_000_000L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-retmet-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private static SQLiteTelemetrySink NewSink(string path) =>
        new(new SQLiteTelemetryOptions
        { DataPath = path, RetentionDays = 0, WalMode = false, AutoVacuum = true, RetentionSweepMinutes = 0 });

    private static HSpan Span(long startNs) =>
        new(new byte[16], new byte[8], null, "s", HSpanKind.Server,
            startNs, startNs + 1_000_000, HStatusCode.Ok, null,
            Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            new HResource(Array.Empty<HAttribute>()), null);

    private static HLogRecord Log(long tsNs) =>
        new(tsNs, HSeverity.Info, "INFO", "b", null, null, Array.Empty<HAttribute>(), null, null);

    [Fact]
    public void ListMetricNames_Enthaelt_Heimdall_Metriken()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var names = sink.ListMetricNames();
            Assert.Contains("heimdall.retention.deleted", names);
            Assert.Contains("heimdall.retention.evicted", names);
            Assert.Contains("heimdall.storage.bytes", names);
            Assert.Contains("heimdall.storage.rows", names);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Storage_Rows_Liefert_Pro_Signal_Die_Zeilenanzahl()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[] { Span(DaysAgoNs(0)), Span(DaysAgoNs(0) + 1), Span(DaysAgoNs(0) + 2) });
            sink.WriteLogs(new[] { Log(DaysAgoNs(0)) });

            var pts = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.storage.rows" }, null, null, null, 100));
            Assert.Equal(3, pts.Count);   // spans, logs, metrics
            var bySignal = pts.ToDictionary(p => p.Labels["signal"], p => (long)p.Value);
            Assert.Equal(3, bySignal["spans"]);
            Assert.Equal(1, bySignal["logs"]);
            Assert.Equal(0, bySignal["metrics"]);
            Assert.All(pts, p => Assert.Equal(HMetricType.Gauge, p.Type));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Storage_Bytes_Liefert_Einen_Punkt_Groesser_Null()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var pts = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.storage.bytes" }, null, null, null, 100));
            var pt = Assert.Single(pts);
            Assert.Equal(HMetricType.Gauge, pt.Type);
            Assert.True(pt.Value > 0);
            Assert.False(pt.Labels.ContainsKey("signal"));   // kein signal-Label
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Retention_Deleted_Zaehlt_Geloeschte_Zeilen_Pro_Signal()
    {
        var path = NewDbPath();
        try
        {
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = path, RetentionDays = 3, WalMode = false, AutoVacuum = true, RetentionSweepMinutes = 0 });

            // 2 alte Spans (>3d → gelöscht), 1 neuer (bleibt).
            sink.WriteSpans(new[] { Span(DaysAgoNs(10)), Span(DaysAgoNs(11)), Span(DaysAgoNs(0)) });
            sink.SweepRetention();

            var pts = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.retention.deleted" }, null, null, null, 100));
            var bySignal = pts.ToDictionary(p => p.Labels["signal"], p => (long)p.Value);
            Assert.Equal(2, bySignal["spans"]);     // 2 gelöscht
            Assert.Equal(0, bySignal["logs"]);
            Assert.Equal(0, bySignal["metrics"]);
            Assert.All(pts, p => Assert.Equal(HMetricType.Sum, p.Type));
            Assert.All(pts, p => Assert.Equal(HTemporality.Cumulative, p.Temporality));
            Assert.Equal(1, sink.CountSpans());     // nur der neue
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Matcher_Signal_Spans_Filtert_Auf_Einen_Punkt()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[] { Span(DaysAgoNs(0)) });

            var pts = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.storage.rows" },
                new[] { new HLabelMatcher("signal", "spans", HMatchOp.Eq) },
                null, null, 100));
            var pt = Assert.Single(pts);
            Assert.Equal("spans", pt.Labels["signal"]);
            Assert.Equal(1, (long)pt.Value);
        }
        finally { Cleanup(path); }
    }
}