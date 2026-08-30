using System;
using System.Collections.Generic;
using System.IO;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer IHeimdallQuery.ListSignalVolume (SQLite-Backend): Basis des
/// Signal-Bandes („Wachtband“) auf der Übersicht. Deckt: Bucket-Bildung via
/// Ganzzahl-Division (ts / bucket) * bucket ueber ALLE drei Signale (Spans via
/// start_unix_nano, Logs/Metrik-Punkte via ts_unix_nano), Sparse-Merge in
/// aufsteigender Reihenfolge (Buckets koennen in mehreren Signalen gleichzeitig
/// vorkommen), Fenster-Respektierung ([from, to] inklusive), Guards (bucket
/// &lt;= 0, invertiertes Fenster) und die Default-Interface-Methode (externe
/// IHeimdallQuery-Implementierer ohne ListSignalVolume liefern leer — die UI
/// zeigt dann flache Null-Lanes, kein Crash).
/// </summary>
public class SignalVolumeTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;
    private static long Minute => 60_000_000_000L;

    /// <summary>Minuten-alignierter „jetzt“-Bucket wie die UI ihn auflöst.</summary>
    private static long AlignedNow => NowNs / Minute * Minute;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-sigvol-" + Guid.NewGuid().ToString("N") + ".db");

    private static SQLiteTelemetrySink NewSql(string path) =>
        new(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0, WalMode = false });

    private static HResource Res(string svc) =>
        new(new[] { new HAttribute("service.name", svc) });

    private static HLogRecord Log(long t, string body) =>
        new(t, HSeverity.Info, "INFO", body, null, null, Array.Empty<HAttribute>(), Res("shop"), null);

    private static HMetricPoint Metric(long t) =>
        new("orders", "1", HMetricType.Sum, HTemporality.Cumulative, t, 1, 1, null, null, null, null, null,
            Array.Empty<HAttribute>(), Res("shop"), null);

    // Deterministische 16-Byte-Trace-ID / 8-Byte-Span-ID (Muster ServiceFilterTests).
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

    private static HSpan Span(int traceSeed, int spanSeed, long start) =>
        new(Tid(traceSeed).id, Sid(spanSeed).id, null, "op", HSpanKind.Server,
            start, start + 1_000_000, HStatusCode.Ok, null,
            Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            Res("shop"), null);

    [Fact]
    public void ListSignalVolume_BucketsUeberZweiMinuten_AlleDreiSignale()
    {
        var path = NewDbPath();
        try
        {
            long a = AlignedNow;               // Bucket A
            long b = a + Minute;               // Bucket B (eine Minute später)
            using var sink = NewSql(path);
            sink.WriteSpans(new[]
            {
                Span(1, 1, a), Span(1, 2, a + 1_000_000),   // 2 Spans in Bucket A
                Span(2, 3, b),                               // 1 Span in Bucket B
            });
            sink.WriteLogs(new[] { Log(a + 2_000_000, "log-a") });
            sink.WriteMetrics(new[]
            {
                Metric(a + 3_000_000),                      // 1 Metrik-Punkt in Bucket A
                Metric(b + 4_000_000),                     // 1 Metrik-Punkt in Bucket B
            });

            var vol = sink.ListSignalVolume(a, b + Minute - 1, Minute);
            Assert.Equal(2, vol.Count);
            // Aufsteigend, Bucket-Anfang minuten-aligniert.
            Assert.Equal(a, vol[0].BucketUnixNano);
            Assert.Equal(b, vol[1].BucketUnixNano);
            // Merge: jedes Signal in seinem Slot, leere Slots = 0.
            Assert.Equal(2, vol[0].Spans);
            Assert.Equal(1, vol[0].Logs);
            Assert.Equal(1, vol[0].Metrics);
            Assert.Equal(1, vol[1].Spans);
            Assert.Equal(0, vol[1].Logs);
            Assert.Equal(1, vol[1].Metrics);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ListSignalVolume_RespektiertFenster_InklusiveGrenzen()
    {
        var path = NewDbPath();
        try
        {
            long a = AlignedNow;
            long old = a - 10 * Minute;                   // deutlich vor dem Fenster
            using var sink = NewSql(path);
            sink.WriteSpans(new[] { Span(1, 1, old), Span(2, 2, a) });

            // Fenster ab a: der alte Span bleibt draußen, der Grenz-Bucket a
            // (from inklusive) bleibt drin.
            var vol = sink.ListSignalVolume(a, a + Minute - 1, Minute);
            Assert.Single(vol);
            Assert.Equal(a, vol[0].BucketUnixNano);
            Assert.Equal(1, vol[0].Spans);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ListSignalVolume_LeereDb_Leer()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            Assert.Empty(sink.ListSignalVolume(0, NowNs, Minute));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ListSignalVolume_Guards_LeeresFensterOderKaputterBucket()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteLogs(new[] { Log(NowNs, "irrelevant") });

            Assert.Empty(sink.ListSignalVolume(0, NowNs, 0));          // bucket <= 0
            Assert.Empty(sink.ListSignalVolume(0, NowNs, -Minute));    // negativer bucket
            Assert.Empty(sink.ListSignalVolume(NowNs, 0, Minute));    // from > to
        }
        finally { TryDelete(path); }
    }

    /// <summary>Minimaler Fremd-Implementierer: ListSignalVolume ist NICHT
    /// ueberschrieben — die Default-Interface-Methode muss leer liefern
    /// (additiv, nicht brechend; externe Backends wie Walhalla laufen weiter).</summary>
    private sealed class VolumeLessQuery : IHeimdallQuery
    {
        public IReadOnlyList<TraceSummary> ListTraces(TraceFilter f) => Array.Empty<TraceSummary>();
        public IReadOnlyList<SpanRow> GetTrace(string t) => Array.Empty<SpanRow>();
        public IReadOnlyList<LogRow> SearchLogs(LogSearch s) => Array.Empty<LogRow>();
        public IReadOnlyList<SpanRow> ListSpans(SpanFilter f) => Array.Empty<SpanRow>();
        public IReadOnlyList<MetricRow> MetricSeries(string n, long? f, long? t, int lim = 500) => Array.Empty<MetricRow>();
        public long CountSpans() => 1;                    // nicht leer — UI rendert das Band
        public long CountLogs() => 0;
        public long CountMetrics() => 0;
    }

    [Fact]
    public void ListSignalVolume_DIM_Default_LiefertLeer()
    {
        IHeimdallQuery q = new VolumeLessQuery();
        Assert.Empty(q.ListSignalVolume(0, NowNs, Minute));
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) try { File.Delete(path); } catch { }
    }
}