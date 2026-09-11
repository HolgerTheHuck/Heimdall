using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Heimdall;
using Heimdall.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Format v2 — Brotli-Kompression der JSON-Spalten (JsonBlob): Roh-Format
/// (typeof='blob', 0x01-Magic), Roundtrip über die Query-APIs, Kompressions-
/// Buchhaltung im Depot-Stat, Attr-Index-Trigger auf komprimierten Spalten
/// (heimd_unzip-UDF) und Histogramm-Roundtrip.
/// </summary>
public class JsonCompressionTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-jsonblob-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private static SQLiteTelemetrySink NewSink(string path, Action<SQLiteTelemetryOptions>? tune = null)
    {
        var o = new SQLiteTelemetryOptions
        {
            DataPath = path, RetentionDays = 0, WalMode = false,
            AutoVacuum = true, RetentionSweepMinutes = 0
        };
        tune?.Invoke(o);
        return new SQLiteTelemetrySink(o);
    }

    private static HResource Res(string svc) => new(new[] { new HAttribute("service.name", svc) });

    private static int _spanSeq;
    private static HSpan MakeSpan(long startNs, string name = "s", bool bigAttrs = false) =>
        new(Tid(1), Sid(Interlocked.Increment(ref _spanSeq)), null, name, HSpanKind.Server,
            startNs, startNs + 1_000_000, HStatusCode.Ok, null,
            bigAttrs
                ? new[] { new HAttribute("payload", new string('x', 2000)), new HAttribute("k", "v") }
                : Array.Empty<HAttribute>(),
            Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            Res("svc"), new HScope("api", "1.0", Array.Empty<HAttribute>()));

    private static HLogRecord MakeLog(long tsNs, string body = "log", string svc = "shop") =>
        new(tsNs, HSeverity.Info, "INFO", body, null, null,
            new[] { new HAttribute("http.route", "/api/x") }, Res(svc), null);

    private static HMetricPoint MakeHist(long tsNs, string name = "h", long[]? counts = null) =>
        new(name, "ms", HMetricType.Histogram, HTemporality.Cumulative, tsNs, 0, 3, 12.5, 1, 9,
            counts ?? new long[] { 10, 20, 30 }, new double[] { 1.0, 5.0, 10.0 },
            Array.Empty<HAttribute>(), null, null);

    private static byte[] Tid(int seed) { var b = new byte[16]; b[0] = 0xa1; b[15] = (byte)seed; return b; }
    private static byte[] Sid(int seed) { var b = new byte[8]; b[0] = (byte)(seed >> 8); b[7] = (byte)seed; return b; }

    /// <summary>Roh-Check direkt an der DB-Datei (zweite Connection): Spalte ist
    /// ein BLOB und beginnt mit dem 0x01-Magic.</summary>
    private static (string Type, bool Magic) RawColumn(string path, string table, string column)
    {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        c.Open();
        using var cmd = new SqliteCommand(
            $"SELECT typeof({column}), substr({column},1,1)=x'01' FROM {table} LIMIT 1", c);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetString(0), r.GetInt64(1) == 1);
    }

    [Fact]
    public void Rohformat_Attrs_Sind_Brotli_Blobs()
    {
        var path = NewDbPath();
        try
        {
            using (var sink = NewSink(path))
            {
                sink.WriteSpans(new[] { MakeSpan(NowNs, bigAttrs: true) });
                sink.WriteLogs(new[] { MakeLog(NowNs, "body") });
                sink.WriteMetrics(new[] { MakeHist(NowNs) });
            }

            var (spanType, spanMagic) = RawColumn(path, "heim_spans", "attrs_json");
            Assert.Equal("blob", spanType);
            Assert.True(spanMagic, "heim_spans.attrs_json beginnt mit 0x01");

            var (logType, logMagic) = RawColumn(path, "heim_logs", "resource_json");
            Assert.Equal("blob", logType);
            Assert.True(logMagic, "heim_logs.resource_json beginnt mit 0x01");

            var (histType, histMagic) = RawColumn(path, "heim_metrics", "bucket_counts_json");
            Assert.Equal("blob", histType);
            Assert.True(histMagic, "heim_metrics.bucket_counts_json beginnt mit 0x01");

            // heim_metric_series bleibt bewusst TEXT (UNIQUE-Equality/Dedup).
            var (seriesType, _) = RawColumn(path, "heim_metric_series", "attrs_json");
            Assert.Equal("text", seriesType);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Roundtrip_Spans_und_Logs_Identisch()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var span = MakeSpan(NowNs, "op", bigAttrs: true);
            sink.WriteSpans(new[] { span });
            sink.WriteLogs(new[] { MakeLog(NowNs, "roundtrip-body") });

            var trace = sink.GetTrace(HeimdallJson.ToHex(span.TraceId));
            var row = Assert.Single(trace);
            // Attr-Payload unversehrt (dekomprimiert) — inkl. 2000-Zeichen-Wert.
            Assert.Equal("op", row.Name);
            Assert.Contains(new string('x', 2000), row.AttrsJson);

            var hits = sink.SearchLogs(new LogSearch { Text = "roundtrip-body", Limit = 10 });
            Assert.Equal("roundtrip-body", Assert.Single(hits).Body);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Attr_Index_Trigger_Greifen_Auf_Komprimierten_Spalten()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            // Attr-Filter auf Log- UND Resource-Attr — beides läuft durch die
            // heimd_unzip-Trigger (ai) bzw. den Backfill (spans).
            sink.WriteLogs(new[] { MakeLog(NowNs, "a"), MakeLog(NowNs, "b", svc: "billing") });
            sink.WriteSpans(new[] { MakeSpan(NowNs, "op", bigAttrs: true) });

            var shop = sink.SearchLogs(new LogSearch
                { AttrFilters = new[] { new AttrFilter("service.name", "=", "shop") }, Limit = 10 });
            Assert.Equal("a", Assert.Single(shop).Body);

            var route = sink.SearchLogs(new LogSearch
                { AttrFilters = new[] { new AttrFilter("http.route", "=", "/api/x") }, Limit = 10 });
            Assert.Equal(2, route.Count);

            // Span-Attr-Index: Service-Discovery über komprimierte resource_json.
            var services = sink.ListServiceNames();
            Assert.Contains("svc", services);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Histogramm_Roundtrip_Ueber_MetricSeries_und_FetchPoints()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var counts = new long[] { 10, 20, 30 };
            sink.WriteMetrics(new[] { MakeHist(NowNs, "h.brotli", counts) });

            var rows = sink.MetricSeries("h.brotli", null, null);
            var row = Assert.Single(rows);
            Assert.NotNull(row.BucketCountsJson);
            Assert.Equal("[10,20,30]", row.BucketCountsJson);

            var points = sink.FetchPoints(new HMetricQuery(
                new[] { "h.brotli" }, null, null, null));
            var p = Assert.Single(points);
            Assert.NotNull(p.BucketCounts);
            Assert.Equal(counts, p.BucketCounts);
            Assert.Equal(new double[] { 1.0, 5.0, 10.0 }, p.ExplicitBounds);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Rollup_Komprimiert_und_Liest_Zurueck()
    {
        var path = NewDbPath();
        try
        {
            // RawDays=0 → roher Punkt rollt beim ersten Sweep in einen Bucket
            // (WriteRollupBatch komprimiert bucket_counts ebenfalls).
            using var sink = NewSink(path, o =>
            {
                o.Rollup = new HeimdallRollupOptions { Enabled = true, ResolutionSeconds = 60, RawDays = 0 };
                o.Retention = new HeimdallRetentionOptions { MetricsDays = 30 };
            });
            long old = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds() * 1_000_000_000L;
            var counts = new long[] { 5, 6, 7 };
            sink.WriteMetrics(new[] { MakeHist(old, "h.roll", counts) });

            sink.SweepRetention();   // rollt (RawDays=0) + löscht die Raw-Zeile

            var points = sink.FetchPoints(new HMetricQuery(new[] { "h.roll" }, null, null, null));
            var p = Assert.Single(points);
            Assert.NotNull(p.BucketCounts);
            Assert.Equal(counts, p.BucketCounts);
            Assert.Equal(new double[] { 1.0, 5.0, 10.0 }, p.ExplicitBounds);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DepotStats_und_Metrik_Zaehlen_Kompression()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[] { MakeSpan(NowNs, bigAttrs: true) });
            sink.WriteLogs(new[] { MakeLog(NowNs, "b") });
            sink.WriteMetrics(new[] { MakeHist(NowNs) });

            var s = sink.GetDepotStats();
            Assert.True(s.CompressionEnabled);
            Assert.True(s.CompressedStoredBytes > 0);
            Assert.True(s.CompressedRawBytes > 0);
            // Brotli auf 2000-x-Payload: massiv kleiner. Tolerant: überhaupt kleiner.
            Assert.True(s.CompressedStoredBytes < s.CompressedRawBytes,
                $"stored {s.CompressedStoredBytes} < raw {s.CompressedRawBytes}");

            // Synth-Metrik: eingesparte Bytes > 0 und gelistet.
            Assert.Contains("heimdall.storage.compression.saved", sink.ListMetricNames());
            var saved = sink.FetchPoints(new HMetricQuery(
                new[] { "heimdall.storage.compression.saved" }, null, null, null));
            var pt = Assert.Single(saved);
            Assert.True(pt.Value > 0);
        }
        finally { Cleanup(path); }
    }
}