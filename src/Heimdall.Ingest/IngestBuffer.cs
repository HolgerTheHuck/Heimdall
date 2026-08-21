using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Heimdall;

namespace Heimdall.Ingest;

/// <summary>
/// Gebundener In-Process-Buffer ueber einen Downstream-<see cref="IHeimdallSink"/>.
/// Sammelt Spans/Logs/Metriken, bildet Batches und flusht diese im Hintergrund.
/// Implementiert selbst <see cref="IHeimdallSink"/> und ist damit das Schreibziel
/// fuer alle drei Ingestion-Pfade (Direct, Sdk, OTLP).
///
/// Rekursionsschutz: Schreibaufrufe, die waehrend eines Flushs (also aus dem
/// Downstream-Sink heraus) erfolgen, werden verworfen (siehe
/// <see cref="HeimdallRecording"/>). Der Flush setzt zusaetzlich die Suppression,
/// damit instrumentierter Storage-Code beim Schreiben nichts aufzeichnet.
/// </summary>
public sealed class IngestBuffer : IHeimdallSink, IDisposable
{
    private readonly IngestOptions _options;
    private readonly IHeimdallSink _downstream;
    private readonly Channel<HSpan> _spans;
    private readonly Channel<HLogRecord> _logs;
    private readonly Channel<HMetricPoint> _metrics;
    private readonly CancellationTokenSource _cts;
    private readonly Task[] _workers;

    // Zaehler fuer beobachtbare Verwerfungen (später: eigene Metrik).
    private long _droppedSpans, _droppedLogs, _droppedMetrics;
    private long _flushedSpans, _flushedLogs, _flushedMetrics;

    public long DroppedSpans => Interlocked.Read(ref _droppedSpans);
    public long DroppedLogs => Interlocked.Read(ref _droppedLogs);
    public long DroppedMetrics => Interlocked.Read(ref _droppedMetrics);
    public long FlushedSpans => Interlocked.Read(ref _flushedSpans);
    public long FlushedLogs => Interlocked.Read(ref _flushedLogs);
    public long FlushedMetrics => Interlocked.Read(ref _flushedMetrics);

    public IngestBuffer(IHeimdallSink downstream, IngestOptions? options = null)
    {
        _downstream = downstream ?? throw new ArgumentNullException(nameof(downstream));
        _options = options ?? new IngestOptions();

        var fullMode = _options.DropPolicy switch
        {
            IngestDropPolicy.DropOldest => BoundedChannelFullMode.DropOldest,
            _ => BoundedChannelFullMode.DropWrite,
        };

        _spans = Channel.CreateBounded<HSpan>(new BoundedChannelOptions(_options.MaxQueueItems)
        { FullMode = fullMode, SingleReader = true, SingleWriter = false });
        _logs = Channel.CreateBounded<HLogRecord>(new BoundedChannelOptions(_options.MaxQueueItems)
        { FullMode = fullMode, SingleReader = true, SingleWriter = false });
        _metrics = Channel.CreateBounded<HMetricPoint>(new BoundedChannelOptions(_options.MaxQueueItems)
        { FullMode = fullMode, SingleReader = true, SingleWriter = false });

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _workers = new Task[]
        {
            Task.Run(() => RunSpansAsync(token), token),
            Task.Run(() => RunLogsAsync(token), token),
            Task.Run(() => RunMetricsAsync(token), token),
        };
    }

    // --- IHeimdallSink -------------------------------------------------------

    public void WriteSpans(IReadOnlyList<HSpan> spans)
    {
        if (spans is null || spans.Count == 0 || HeimdallRecording.IsSuppressed) return;
        Enqueue(_spans, spans, ref _droppedSpans);
    }

    public void WriteLogs(IReadOnlyList<HLogRecord> logs)
    {
        if (logs is null || logs.Count == 0 || HeimdallRecording.IsSuppressed) return;
        Enqueue(_logs, logs, ref _droppedLogs);
    }

    public void WriteMetrics(IReadOnlyList<HMetricPoint> metrics)
    {
        if (metrics is null || metrics.Count == 0 || HeimdallRecording.IsSuppressed) return;
        Enqueue(_metrics, metrics, ref _droppedMetrics);
    }

    private void Enqueue<T>(Channel<T> ch, IReadOnlyList<T> items, ref long dropped)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (!ch.Writer.TryWrite(items[i]))
                Interlocked.Increment(ref dropped);
        }
    }

    // --- Hintergrund-Worker --------------------------------------------------

    private async Task RunSpansAsync(CancellationToken token)
        => await RunAsync(_spans, _downstream.WriteSpans, _options.BatchSpans, n => Interlocked.Add(ref _flushedSpans, n), token).ConfigureAwait(false);

    private async Task RunLogsAsync(CancellationToken token)
        => await RunAsync(_logs, _downstream.WriteLogs, _options.BatchLogs, n => Interlocked.Add(ref _flushedLogs, n), token).ConfigureAwait(false);

    private async Task RunMetricsAsync(CancellationToken token)
        => await RunAsync(_metrics, _downstream.WriteMetrics, _options.BatchMetrics, n => Interlocked.Add(ref _flushedMetrics, n), token).ConfigureAwait(false);

    private async Task RunAsync<T>(
        Channel<T> channel,
        Action<IReadOnlyList<T>> flush,
        int batchSize,
        Action<long> addFlushed,
        CancellationToken token)
    {
        var batch = new List<T>(batchSize);
        try
        {
            while (await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                // Alles sofort Verfuegbare bis batchSize lesen, dann flushen.
                // Teil-Batches werden am Ende jeder Drain-Phase sofort ausgegeben
                // => Latenz ist durch die Drain-Dauer gebunden, kein extra Timer noetig.
                batch.Clear();
                while (batch.Count < batchSize && channel.Reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count > 0)
                {
                    FlushSafe(flush, batch);
                    addFlushed(batch.Count);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ChannelClosedException) { /* shutdown */ }
        finally
        {
            // Rest bei Shutdown flushen.
            while (channel.Reader.TryRead(out var tail))
            {
                batch.Add(tail);
                if (batch.Count >= batchSize)
                {
                    FlushSafe(flush, batch);
                    addFlushed(batch.Count);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                FlushSafe(flush, batch);
                addFlushed(batch.Count);
            }
        }
    }

    private void FlushSafe<T>(Action<IReadOnlyList<T>> flush, IReadOnlyList<T> batch)
    {
        // Rekursionsschutz: der Downstream-Sink (Storage) darf beim Schreiben
        // keine Telemetrie erzeugen, die wieder hier landet.
        using (HeimdallRecording.SuppressScope())
        {
            try { flush(batch); }
            catch { /* ein fehlerhafter Batch darf den Worker nicht killen */ }
        }
    }

    /// <summary>Blockierender Flush aller noch gepufferten Items (fuer Tests/Shutdown).</summary>
    public void Flush(TimeSpan timeout)
    {
        DrainImmediate(_spans, _downstream.WriteSpans);
        DrainImmediate(_logs, _downstream.WriteLogs);
        DrainImmediate(_metrics, _downstream.WriteMetrics);
    }

    private void DrainImmediate<T>(Channel<T> channel, Action<IReadOnlyList<T>> flush)
    {
        var batch = new List<T>();
        while (channel.Reader.TryRead(out var item))
        {
            batch.Add(item);
            if (batch.Count >= 64)
            {
                using (HeimdallRecording.SuppressScope()) { try { flush(batch); } catch { } }
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            using (HeimdallRecording.SuppressScope()) { try { flush(batch); } catch { } }
        }
    }

    public void Dispose()
    {
        _spans.Writer.TryComplete();
        _logs.Writer.TryComplete();
        _metrics.Writer.TryComplete();
        _cts.Cancel();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}