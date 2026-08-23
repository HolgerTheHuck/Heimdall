using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Workstream A — Storage & Retention: Per-Signal-TTL (A1), Gesamt-Cap mit
/// Eviction (A2), Space-Reclaim via auto_vacuum/VACUUM-Migration (A3) und
/// Options-Validierung. Triggert den Sweep direkt (SweepRetention ist internal
/// via InternalsVisibleTo); der Timer bleibt ungetestet (Zeit-abhängig).
/// </summary>
public class RetentionTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;
    // Sekunden-granular (wie der Sink-Cutoff) — tagesweise Offsets sind boundary-sicher.
    private static long DaysAgoNs(int d) => DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeSeconds() * 1_000_000_000L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-ret-" + Guid.NewGuid().ToString("N") + ".db");

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
            AutoVacuum = true, RetentionSweepMinutes = 0   // Timer aus; Sweep per Hand.
        };
        tune?.Invoke(o);
        return new SQLiteTelemetrySink(o);
    }

    private static HResource Res(string svc) => new(new[] { new HAttribute("service.name", svc) });

    private static int _spanSeq;
    private static HSpan MakeSpan(long startNs, string name = "s") =>
        new(Tid(1), Sid(Interlocked.Increment(ref _spanSeq)), null, name, HSpanKind.Server,
            startNs, startNs + 1_000_000, HStatusCode.Ok, null,
            new[] { new HAttribute("payload", new string('x', 200)) },
            Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            Res("svc"), new HScope("api", "1.0", Array.Empty<HAttribute>()));

    private static HLogRecord MakeLog(long tsNs, string body = "log") =>
        new(tsNs, HSeverity.Info, "INFO", body, null, null,
            Array.Empty<HAttribute>(), null, null);

    private static HMetricPoint MakeMetric(long tsNs, string name = "m") =>
        new(name, "1", HMetricType.Sum, HTemporality.Cumulative, tsNs, 1, 1, null, null, null, null, null,
            Array.Empty<HAttribute>(), null, null);

    private static byte[] Tid(int seed) { var b = new byte[16]; b[0] = 0xa1; b[15] = (byte)seed; return b; }
    private static byte[] Sid(int seed) { var b = new byte[8]; b[0] = (byte)(seed >> 8); b[7] = (byte)seed; return b; }

    // -----------------------------------------------------------------------
    // A1 — Per-Signal-TTL
    // -----------------------------------------------------------------------

    [Fact]
    public void Per_Signal_TTL_Loescht_Nur_Ueber_Ihrer_Frist()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o => o.Retention = new HeimdallRetentionOptions
            {
                TracesDays = 3, LogsDays = 14, MetricsDays = 30
            });

            // Traces: 10d (gelöscht, >3), 1d (bleibt).
            sink.WriteSpans(new[] { MakeSpan(DaysAgoNs(10), "alt"), MakeSpan(DaysAgoNs(1), "neu") });
            // Logs: 20d (gelöscht, >14), 10d (bleibt, <14 — länger als Traces), 1d (bleibt).
            sink.WriteLogs(new[] { MakeLog(DaysAgoNs(20), "alt"), MakeLog(DaysAgoNs(10), "mittel"), MakeLog(DaysAgoNs(1), "neu") });
            // Metrics: 40d (gelöscht, >30), 10d (bleibt, <30), 1d (bleibt).
            sink.WriteMetrics(new[] { MakeMetric(DaysAgoNs(40), "alt"), MakeMetric(DaysAgoNs(10), "mittel"), MakeMetric(DaysAgoNs(1), "neu") });

            sink.SweepRetention();

            Assert.Equal(1, sink.CountSpans());     // nur neu
            Assert.Equal(2, sink.CountLogs());      // mittel + neu
            Assert.Equal(2, sink.CountMetrics());   // mittel + neu
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Legacy_Fallback_RetentionDays_Gilt_Fuer_alle_Signale()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o => o.RetentionDays = 5);  // Retention.* bleibt null → Fallback.

            sink.WriteSpans(new[] { MakeSpan(DaysAgoNs(10), "alt"), MakeSpan(DaysAgoNs(1), "neu") });
            sink.WriteLogs(new[] { MakeLog(DaysAgoNs(10)), MakeLog(DaysAgoNs(1)) });
            sink.WriteMetrics(new[] { MakeMetric(DaysAgoNs(10)), MakeMetric(DaysAgoNs(1)) });

            sink.SweepRetention();

            Assert.Equal(1, sink.CountSpans());
            Assert.Equal(1, sink.CountLogs());
            Assert.Equal(1, sink.CountMetrics());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Explizit_Null_TracesDays_Uebersteuert_Den_Fallback()
    {
        var path = NewDbPath();
        try
        {
            // RetentionDays=7 Fallback, aber TracesDays=0 = explizit unbegrenzt.
            using var sink = NewSink(path, o =>
            {
                o.RetentionDays = 7;
                o.Retention = new HeimdallRetentionOptions { TracesDays = 0 };  // Logs/Metrics → Fallback 7.
            });

            sink.WriteSpans(new[] { MakeSpan(DaysAgoNs(10), "alt"), MakeSpan(DaysAgoNs(1), "neu") });
            sink.WriteLogs(new[] { MakeLog(DaysAgoNs(10)), MakeLog(DaysAgoNs(1)) });
            sink.WriteMetrics(new[] { MakeMetric(DaysAgoNs(10)), MakeMetric(DaysAgoNs(1)) });

            sink.SweepRetention();

            Assert.Equal(2, sink.CountSpans());     // unbegrenzt → beide bleiben
            Assert.Equal(1, sink.CountLogs());      // 7-Tage-Frist → alt gelöscht
            Assert.Equal(1, sink.CountMetrics());
        }
        finally { Cleanup(path); }
    }

    // -----------------------------------------------------------------------
    // A2 — Gesamt-Cap mit Eviction
    // -----------------------------------------------------------------------

    [Fact]
    public void Cap_Evictiert_Aelteste_Bis_MaxBytes()
    {
        const int N = 2000;
        // 1. Auf einer separaten DB Overhead + Vollaufbau messen (der Sink kann
        //    MaxBytes nur im Ctor setzen, darum vorab bemessen). FTS5-Shadow-Pages
        //    sind Teil der Belegung — der Margin muss sie mit decken.
        var op = NewDbPath();
        long overhead, uFull;
        using (var os = NewSink(op))
        {
            overhead = os.UsedBytes();
            os.WriteSpans(MakeSpans(N));
            uFull = os.UsedBytes();
        }
        Cleanup(op);
        Assert.True(uFull > overhead);

        // 2. MaxBytes auf ~80 % des Datenanteils → erzwingt Eviction, lässt Rows.
        long maxBytes = overhead + (uFull - overhead) * 4 / 5;
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o => o.MaxBytes = maxBytes);
            sink.WriteSpans(MakeSpans(N));

            Assert.True(sink.UsedBytes() > maxBytes);    // überhaupt über dem Cap

            sink.SweepRetention();                        // Eviction + FTS-Rebuild + incremental_vacuum

            Assert.True(sink.UsedBytes() <= maxBytes,    // Cap eingehalten
                $"UsedBytes {sink.UsedBytes()} > MaxBytes {maxBytes}");
            Assert.True(sink.CountSpans() < N);          // evictet
            Assert.True(sink.CountSpans() > 0);          // nicht alles weg
        }
        finally { Cleanup(path); }
    }

    // N Spans mit aufsteigendem Zeitstempel (i=0 am ältesten) und ~200-Byte-Payload.
    private static List<HSpan> MakeSpans(int n)
    {
        var spans = new List<HSpan>(n);
        long base0 = DaysAgoNs(0);
        for (int i = 0; i < n; i++) spans.Add(MakeSpan(base0 + i, "op-" + i));
        return spans;
    }

    // -----------------------------------------------------------------------
    // A3 — Space-Reclaim
    // -----------------------------------------------------------------------

    [Fact]
    public void Frische_DB_Hat_AutoVacuum_Und_UserVersion_1()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            Assert.Equal(2, sink.PragmaLong("auto_vacuum"));   // INCREMENTAL
            Assert.Equal(1, sink.PragmaLong("user_version"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void IncrementalVacuum_Schrumpft_Datei_Nach_Sweep()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o => o.RetentionDays = 5);  // TTL 5 Tage

            var spans = new List<HSpan>();
            for (int i = 0; i < 300; i++) spans.Add(MakeSpan(DaysAgoNs(10) + i, "s" + i));  // alle 10d → werden gelöscht
            sink.WriteSpans(spans);

            long sizeBefore = new FileInfo(path).Length;
            Assert.True(sizeBefore > 0);

            sink.SweepRetention();   // löscht alle (alt) + incremental_vacuum → Datei schrumpft

            long sizeAfter = new FileInfo(path).Length;
            Assert.True(sizeAfter < sizeBefore, $"Datei nicht geschrumpft: {sizeAfter} >= {sizeBefore}");
            Assert.Equal(0, sink.CountSpans());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Legacy_DB_Wird_Einmalig_Vacuum_Migriert()
    {
        var path = NewDbPath();
        try
        {
            // 1. Legacy-DB: AutoVacuum=false → kein auto_vacuum, kein user_version.
            using (var legacy = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
                { DataPath = path, RetentionDays = 0, WalMode = false, AutoVacuum = false, RetentionSweepMinutes = 0 }))
            {
                legacy.WriteSpans(new[] { MakeSpan(DaysAgoNs(0), "legacy") });
                Assert.Equal(0, legacy.PragmaLong("user_version"));
                Assert.Equal(0, legacy.PragmaLong("auto_vacuum"));
            }

            // 2. Mit AutoVacuum=true + Migrate öffnen → einmalige VACUUM-Migration.
            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
                { DataPath = path, RetentionDays = 0, WalMode = false, AutoVacuum = true, VacuumMigrateLegacy = true, RetentionSweepMinutes = 0 });
            Assert.Equal(2, sink.PragmaLong("auto_vacuum"));
            Assert.Equal(1, sink.PragmaLong("user_version"));
            Assert.Equal(1, sink.CountSpans());   // Bestand bleibt erhalten
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Legacy_DB_Mit_Notaus_Wird_Nicht_Migriert()
    {
        var path = NewDbPath();
        try
        {
            using (var legacy = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
                { DataPath = path, RetentionDays = 0, WalMode = false, AutoVacuum = false, RetentionSweepMinutes = 0 }))
                legacy.WriteSpans(new[] { MakeSpan(DaysAgoNs(0), "legacy") });

            using var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
                { DataPath = path, RetentionDays = 0, WalMode = false, AutoVacuum = true, VacuumMigrateLegacy = false, RetentionSweepMinutes = 0 });
            Assert.Equal(0, sink.PragmaLong("user_version"));   // nicht migriert → bleibt migrierbar
            Assert.Equal(1, sink.CountSpans());
        }
        finally { Cleanup(path); }
    }

    // -----------------------------------------------------------------------
    // Validierung (authoritativ im Sink-ctor)
    // -----------------------------------------------------------------------

    [Fact]
    public void Negative_RetentionDays_Wirft_Im_Ctor()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = NewDbPath(), RetentionDays = -1, RetentionSweepMinutes = 0 }));
    }

    [Fact]
    public void Negative_Per_Signal_Retention_Wirft_Im_Ctor()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = NewDbPath(), RetentionDays = 0, RetentionSweepMinutes = 0,
              Retention = new HeimdallRetentionOptions { LogsDays = -2 } }));
    }

    [Fact]
    public void Negatives_MaxBytes_Wirft_Im_Ctor()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = NewDbPath(), RetentionDays = 0, RetentionSweepMinutes = 0, MaxBytes = -1 }));
    }

    [Fact]
    public void Negatives_SweepMinutes_Wirft_Im_Ctor()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SQLiteTelemetrySink(new SQLiteTelemetryOptions
            { DataPath = NewDbPath(), RetentionDays = 0, RetentionSweepMinutes = -1 }));
    }
}