using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer das SQLite-Backend: Schema-Bootstrap, parametrisierte Batch-Inserts,
/// FTS5-Volltextsuche (MATCH) und die IHeimdallQuery-Leseseite.
/// </summary>
public class SQLiteTelemetrySinkTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-sqlite-" + Guid.NewGuid().ToString("N") + ".db");

    private static HResource Res(string svc) =>
        new(new[] { new HAttribute("service.name", svc) });

    // Deterministische 16-Byte-Trace-ID / 8-Byte-Span-ID aus einem Seed;
    // liefert gleichzeitig das kanonische Kleinbuchstaben-Hex (wie der Sink speichert).
    private static (byte[] id, string hex) Tid(int seed)
    {
        var b = new byte[16];
        b[0] = 0xa1; b[15] = (byte)seed;
        return (b, Hex(b));
    }
    private static (byte[] id, string hex) Sid(int seed)
    {
        var b = new byte[8];
        b[0] = (byte)(seed >> 8); b[7] = (byte)seed;
        return (b, Hex(b));
    }
    private static string Hex(byte[] b)
    {
        const string h = "0123456789abcdef";
        var sb = new System.Text.StringBuilder(b.Length * 2);
        for (int i = 0; i < b.Length; i++) { sb.Append(h[b[i] >> 4]); sb.Append(h[b[i] & 0xF]); }
        return sb.ToString();
    }

    private static HSpan MakeSpan(int traceSeed, int spanSeed, int? parentSeed, string name, bool error = false, string svc = "shop", long? start = null)
    {
        var (tid, _) = Tid(traceSeed);
        var (sid, _) = Sid(spanSeed);
        byte[]? parent = parentSeed is null ? null : Sid(parentSeed.Value).id;
        var s = start ?? NowNs;
        return new HSpan(tid, sid, parent, name, HSpanKind.Server,
            s, s + 1_000_000, error ? HStatusCode.Error : HStatusCode.Ok, error ? "boom" : null,
            Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            Res(svc), new HScope("api", "1.0", Array.Empty<HAttribute>()));
    }

    [Fact]
    public void Writes_And_Queries_Traces()
    {
        var path = NewDbPath();
        try
        {
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });

            var (t1, t1hex) = Tid(1);
            var (s1, s1hex) = Sid(1);
            var (s2, _) = Sid(2);
            var (t2, t2hex) = Tid(2);
            sink.WriteSpans(new[]
            {
                new HSpan(t1, s1, null, "checkout", HSpanKind.Server, NowNs, NowNs + 1_000_000, HStatusCode.Ok, null,
                    Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
                    Res("shop"), new HScope("api","1.0", Array.Empty<HAttribute>())),
                new HSpan(t1, s2, s1, "db.query", HSpanKind.Internal, NowNs+1_000_000, NowNs+2_000_000, HStatusCode.Ok, null,
                    Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
                    Res("shop"), new HScope("api","1.0", Array.Empty<HAttribute>())),
                new HSpan(t2, Sid(3).id, null, "login", HSpanKind.Server, NowNs, NowNs+1_000_000, HStatusCode.Error, "bad",
                    Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
                    Res("auth"), new HScope("api","1.0", Array.Empty<HAttribute>())),
            });

            Assert.Equal(3, sink.CountSpans());

            var traces = sink.ListTraces(new TraceFilter { Limit = 100 });
            Assert.Equal(2, traces.Count);
            Assert.Contains(traces, t => t.TraceId == t1hex && t.SpanCount == 2);
            Assert.Contains(traces, t => t.HasError && t.TraceId == t2hex);

            var tree = sink.GetTrace(t1hex);
            Assert.Equal(2, tree.Count);
            Assert.Contains(tree, s => s.ParentSpanId == s1hex);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Fulltext_Match_On_Span_Name()
    {
        var path = NewDbPath();
        try
        {
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });
            var (t1, t1hex) = Tid(11);
            var (t2, _) = Tid(12);
            sink.WriteSpans(new[]
            {
                new HSpan(t1, Sid(1).id, null, "checkout cart", HSpanKind.Server, NowNs, NowNs+1, HStatusCode.Ok, null,
                    Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(), Res("shop"), null),
                new HSpan(t2, Sid(2).id, null, "user login", HSpanKind.Server, NowNs, NowNs+1, HStatusCode.Ok, null,
                    Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(), Res("shop"), null),
            });

            var matches = sink.ListTraces(new TraceFilter { NameContains = "checkout", Limit = 50 });
            Assert.Single(matches);
            Assert.Equal(t1hex, matches[0].TraceId);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Logs_Fulltext_Search()
    {
        var path = NewDbPath();
        try
        {
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });
            sink.WriteLogs(new[]
            {
                new HLogRecord(NowNs, HSeverity.Info, "INFO", "order placed for alice", null, null,
                    Array.Empty<HAttribute>(), null, null),
                new HLogRecord(NowNs + 1, HSeverity.Error, "ERROR", "payment failed for bob", null, null,
                    Array.Empty<HAttribute>(), null, null),
            });

            var hits = sink.SearchLogs(new LogSearch { Text = "payment" });
            Assert.Single(hits);
            Assert.Contains("payment", hits[0].Body);

            var errs = sink.SearchLogs(new LogSearch { MinSeverity = (int)HSeverity.Error });
            Assert.Single(errs);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Metrics_Series_Roundtrip()
    {
        var path = NewDbPath();
        try
        {
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });
            var t0 = NowNs;
            sink.WriteMetrics(new[]
            {
                new HMetricPoint("orders", "1", HMetricType.Sum, HTemporality.Cumulative, t0, 1, 1, null, null, null, null, null,
                    new[] { new HAttribute("region", "eu") }, null, null),
                new HMetricPoint("orders", "1", HMetricType.Sum, HTemporality.Cumulative, t0 + 1, 3, 3, null, null, null, null, null,
                    new[] { new HAttribute("region", "eu") }, null, null),
                new HMetricPoint("lat", "ms", HMetricType.Histogram, HTemporality.Cumulative, t0, 0, 1, 5, 5, 5,
                    new long[] { 0, 1, 1 }, new double[] { 0, 10 }, Array.Empty<HAttribute>(), null, null),
            });

            Assert.Equal(3, sink.CountMetrics());
            var series = sink.MetricSeries("orders", null, null, 10);
            Assert.Equal(2, series.Count);
            Assert.Equal(1, series[0].Value);
            Assert.Equal(3, series[1].Value);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    /// <summary>
    /// Regression: MetricSeries mit null/leerem Namen darf nicht werfen (früher
    /// „InvalidOperationException: Value must be set" aus der SQLite-Bindung, weil
    /// Param("@n", null) den Parameter ohne Value ließ). Aufrufer wie das Dashboard
    /// rufen MetricSeries für einen optionalen Errors-Counter auf, der leer sein darf.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MetricSeries_LeererName_LiefertLeer_StattZuWerfen(string? name)
    {
        var path = NewDbPath();
        try
        {
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });
            sink.WriteMetrics(new[]
            {
                new HMetricPoint("orders", "1", HMetricType.Sum, HTemporality.Cumulative, NowNs, 1, 1, null, null, null, null, null,
                    Array.Empty<HAttribute>(), null, null),
            });
            var series = sink.MetricSeries(name!, null, null, 10);
            Assert.Empty(series);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }
}