using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// C4 — Graceful-Shutdown-Härtung am SQLite-Sink: <c>Dispose()</c> nimmt
/// <c>_gate</c> (serialisiert mit Writes), und <c>Write*</c> prüft nach Lock-
/// Aquisition nochmals <c>_disposed</c> (Double-Check). Writes nach Dispose sind
/// Noops (kein <c>_conn</c>-Zugriff, kein Throw); Dispose nebenläufig zu Writes
/// wirft nicht. Kein In-Memory-Puffer im Sink — Flush-Verifikation des Puffer-
/// Pfads siehe <see cref="IngestBufferTests"/>.
/// </summary>
public class SinkShutdownTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-shutdown-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private static SQLiteTelemetrySink NewSink(string path) =>
        new(new SQLiteTelemetryOptions
        { DataPath = path, RetentionDays = 0, WalMode = false, AutoVacuum = true, RetentionSweepMinutes = 0 });

    private static HSpan Span() =>
        new(new byte[16], new byte[8], null, "s", HSpanKind.Server,
            NowNs, NowNs + 1_000_000, HStatusCode.Ok, null,
            Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            new HResource(Array.Empty<HAttribute>()), null);

    [Fact]
    public void Write_Nach_Dispose_Ist_Noop_Ohne_Throw()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[] { Span() });   // eine Zeile (Beweis, dass der Sink lebt)
            Assert.Equal(1, sink.CountSpans());   // Pre-Dispose-Zählerstand
            sink.Dispose();

            // Schreiben nach Dispose darf nicht werfen und nichts persistieren.
            var ex = Record.Exception(() => sink.WriteSpans(new[] { Span(), Span() }));
            Assert.Null(ex);

            // Post-Dispose-Lesen über den Sink wirft (geschlossenes _conn) — darum
            // Zählerstand über eine frische Verbindung prüfen: nur die Pre-Dispose-Zeile.
            Assert.Equal(1L, RawCount(path, "heim_spans"));
        }
        finally { Cleanup(path); }
    }

    private static long RawCount(string path, string table)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Dispose_Idempotent_Mehrfach()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.Dispose();
            var ex = Record.Exception(() => sink.Dispose());
            Assert.Null(ex);   // Double-Dispose via Interlocked.Exchange → Noop
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Dispose_Nebenchlaeufig_Zu_Writes_Wirft_Nicht()
    {
        var path = NewDbPath();
        try
        {
            var sink = NewSink(path);
            var cts = new CancellationTokenSource();
            Exception? writeEx = null;

            // Dauerfeuer an Writes (kontrolliert über das Token), Dispose läuft nebenbei.
            var writer = Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                        sink.WriteSpans(new[] { Span() });
                }
                catch (Exception e) { writeEx = e; }
            });

            // Den Writer kurz laufen lassen, dann Dispose — der Double-Check im Lock
            // muss sicherstellen, dass kein Write mehr _conn berührt.
            await Task.Delay(50);
            sink.Dispose();
            cts.Cancel();
            await writer;

            Assert.Null(writeEx);   // kein Write hat nach Dispose geworfen
        }
        finally { Cleanup(path); }
    }
}