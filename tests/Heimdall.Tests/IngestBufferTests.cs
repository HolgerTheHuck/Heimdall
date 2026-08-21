using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Heimdall;
using Heimdall.Ingest;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Verifiziert den IngestBuffer: Batching, Hintergrund-Flush in den
/// SQLite-Sink und Rekursionsschutz auf Puffer-Ebene. Der IngestBuffer ist
/// backend-agnostisch (nimmt IHeimdallSink); SQLite vertritt hier das Backend.
/// </summary>
public class IngestBufferTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-ingest-" + Guid.NewGuid().ToString("N") + ".db");

    private static HSpan MakeSpan(int i)
    {
        var t = new byte[16]; t[15] = (byte)i;
        var s = new byte[8]; s[0] = (byte)i;
        return new HSpan(t, s, null, "op" + i, HSpanKind.Internal,
            NowNs, NowNs, HStatusCode.Ok, null,
            Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            null, null);
    }

    private static SQLiteTelemetrySink NewSink(string path) =>
        new(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0, WalMode = false });

    [Fact]
    public void Buffer_Flushes_Spans_To_Sqlite_Sink()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            using var buffer = new IngestBuffer(sink, new IngestOptions
            { BatchSpans = 8, FlushIntervalMs = 50, MaxQueueItems = 10_000 });

            for (int i = 0; i < 25; i++)
                buffer.WriteSpans(new[] { MakeSpan(i) });

            // Warten, bis die Hintergrund-Worker die Batches geflusht haben.
            WaitUntil(() => sink.CountSpans() >= 25);

            Assert.Equal(25L, sink.CountSpans());
            Assert.Equal(25, buffer.FlushedSpans);
            Assert.Equal(0, buffer.DroppedSpans);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Buffer_Drops_Writes_Made_Under_Suppression()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            using var buffer = new IngestBuffer(sink, new IngestOptions
            { BatchSpans = 64, FlushIntervalMs = 50, MaxQueueItems = 10_000 });

            // Schreiben unter Suppression (simuliert Telemetrie aus dem Schreibpfad)
            // muss verworfen werden -> kein Feedback-Loop.
            using (HeimdallRecording.SuppressScope())
            {
                for (int i = 0; i < 10; i++)
                    buffer.WriteSpans(new[] { MakeSpan(i) });
            }
            Thread.Sleep(300);

            Assert.Equal(0L, sink.CountSpans());
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    private static void WaitUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            Thread.Sleep(20);
        }
    }
}