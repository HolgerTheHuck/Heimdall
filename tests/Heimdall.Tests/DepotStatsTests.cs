using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Depot-Übersicht (GetDepotStats): Dateigrößen (DB + WAL), Page-Statistik,
/// Verbrauch je Tabelle/-gruppe via dbstat, Fallback ohne dbstat (Bytes = -1),
/// und die effektive Retention-/Cap-Konfiguration im DTO.
/// </summary>
public class DepotStatsTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-depot-" + Guid.NewGuid().ToString("N") + ".db");

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
            AutoVacuum = true, RetentionSweepMinutes = 0   // Timer aus.
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

    [Fact]
    public void DepotStats_Nach_Writes_Liefert_Tabellen_Bytes_und_Pages()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o => o.MaxBytes = 1_000_000_000);
            sink.WriteSpans(new[] { MakeSpan(NowNs), MakeSpan(NowNs) });
            sink.WriteLogs(new[] { MakeLog(NowNs) });
            sink.WriteMetrics(new[] { MakeMetric(NowNs) });

            var s = sink.GetDepotStats();

            Assert.True(s.UsedBytes > 0);
            Assert.Equal(1_000_000_000, s.MaxBytes);
            Assert.True(s.PageSize > 0);
            Assert.True(s.PageCount > 0);
            Assert.True(s.TotalLogicalBytes >= s.UsedBytes);

            var spans = Assert.Single(s.Tables, t => t.Table == "heim_spans");
            Assert.Equal(2, spans.Rows);
            Assert.Equal(sink.CountSpans(), spans.Rows);
            // Bytes: dbstat (echte Pages) ODER Schätzung (logische Datenmenge) —
            // in beiden Fällen > 0, sobald Zeilen geschrieben wurden. Das gebündelte
            // e_sqlite3 von Microsoft.Data.Sqlite hat kein dbstat → hier Schätzpfad.
            Assert.True(spans.Bytes > 0);

            var logs = Assert.Single(s.Tables, t => t.Table == "heim_logs");
            Assert.Equal(1, logs.Rows);
            Assert.True(logs.Bytes > 0);

            var metrics = Assert.Single(s.Tables, t => t.Table == "heim_metrics");
            Assert.Equal(1, metrics.Rows);
            Assert.True(metrics.Bytes > 0);

            if (s.DbStatAvailable)
            {
                // dbstat-Modus: FTS-Shadow-Gruppen + _sonstige als Einträge (Rows = -1).
                Assert.Equal(-1, Assert.Single(s.Tables, t => t.Table == "heim_spans_fts*").Rows);
                Assert.Contains(s.Tables, t => t.Table == "_sonstige" && t.Bytes >= 0);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DepotStats_Freelist_und_Dateigroesse_Sind_Konsistent()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[] { MakeSpan(NowNs) });

            var s = sink.GetDepotStats();

            Assert.Equal(sink.UsedBytes(), s.UsedBytes);
            Assert.Equal(s.PageSize * s.PageCount, s.TotalLogicalBytes);
            Assert.Equal(s.FreelistCount * s.PageSize, s.FreelistBytes);
            if (File.Exists(path))
                Assert.True(s.FileBytes > 0, "DB-Datei existiert → FileBytes > 0");
            // Summe der Gruppen liegt unterhalb von Datei + WAL (Datei enthält Header/Overhead).
            Assert.True(s.Tables.Sum(t => t.Bytes) <= s.FileBytes + s.WalBytes + 65536);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DepotStats_Leere_DB_Liefert_Nur_Nullen_Keine_Exception()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);

            var s = sink.GetDepotStats();

            // Leere, gebootstrappte DB: Schema-Pages (FTS/Indexe) belegen trotzdem
            // etwas — UsedBytes ist klein, aber nicht zwingend 0.
            Assert.InRange(s.UsedBytes, 0, 1_048_576);
            // Basistabellen existieren, haben aber 0 Zeilen (dbstat-Schätzung gleich).
            Assert.Equal(0, Assert.Single(s.Tables, t => t.Table == "heim_spans").Rows);
            Assert.Equal(0, Assert.Single(s.Tables, t => t.Table == "heim_logs").Rows);
            Assert.Equal(0, Assert.Single(s.Tables, t => t.Table == "heim_spans").Bytes);
            Assert.True(s.CompressionEnabled);    // Format v2: JSON-Spalten immer Brotli-BLOB
            Assert.Equal(0, s.CompressedStoredBytes);
            Assert.Equal(0, s.CompressedRawBytes);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DepotStats_Trage_Effektive_Retention_Konfiguration()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path, o =>
            {
                o.Retention = new HeimdallRetentionOptions { TracesDays = 3, LogsDays = 14, MetricsDays = 30 };
                o.MaxBytes = 512 * 1024 * 1024;
                o.RetentionSweepMinutes = 15;
                o.Rollup = new HeimdallRollupOptions { Enabled = true, ResolutionSeconds = 120 };
            });

            var s = sink.GetDepotStats();

            Assert.Equal(3, s.TracesDays);
            Assert.Equal(14, s.LogsDays);
            Assert.Equal(30, s.MetricsDays);
            Assert.Equal(512 * 1024 * 1024, s.MaxBytes);
            Assert.Equal(15, s.SweepMinutes);
            Assert.True(s.RollupEnabled);
            Assert.Equal(120, s.RollupSeconds);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DepotStats_WAL_Datei_Wird_Erfasst()
    {
        var path = NewDbPath();
        try
        {
            // WAL an: schreibende Verbindung lässt die -wal-Datei bestehen.
            using var sink = NewSink(path, o => o.WalMode = true);
            sink.WriteLogs(new[] { MakeLog(NowNs, "wal-check") });

            var s = sink.GetDepotStats();
            // WAL kann bereits gecheckpointet sein — aber wenn die Datei da ist,
            // muss sie im Statistikwert stecken.
            if (File.Exists(path + "-wal"))
                Assert.True(s.WalBytes > 0, "WAL-Datei existiert → WalBytes > 0");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DepotStats_Empty_Ist_Der_Interface_Default()
    {
        // Ein IHeimdallQuery-Implementierer ohne Depot-Sicht liefert die leere
        // Struktur (DbStatAvailable=false) — der UI-Zustand „nicht verfügbar".
        var empty = DepotStats.Empty;
        Assert.False(empty.DbStatAvailable);
        Assert.False(empty.CompressionEnabled);
        Assert.Empty(empty.Tables);
    }
}