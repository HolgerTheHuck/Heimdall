using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;
using SelfLog = Serilog.Debugging.SelfLog;

namespace Serilog.Sinks.Heimdall;

/// <summary>
/// Batched-Sink (Serilog 4 natives Batching, registriert via WriteTo.Sink):
/// sammelt Events, baut pro Batch einen OTLP-ExportLogs-Request und schickt ihn
/// über den Transmitter an Heimdall. Werferfrei — ein Fehler verwirft den Batch
/// und wird per <see cref="SelfLog"/> gemeldet, damit Logging niemals die App
/// beeinträchtigt (Haus-Stil wie der Heimdall.Sdk-Exporter).
/// </summary>
internal sealed class HeimdallBatchedSink : IBatchedLogEventSink, IDisposable
{
    private readonly IHeimdallLogTransmitter _transmitter;
    private readonly HeimdallSinkOptions _options;

    public HeimdallBatchedSink(IHeimdallLogTransmitter transmitter, HeimdallSinkOptions options)
    {
        _transmitter = transmitter;
        _options = options;
    }

    public Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
    {
        try
        {
            var request = OtlpLogRequestBuilder.Build(batch, _options);
            if (request.ResourceLogs.Count > 0) _transmitter.Transmit(request);
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("Heimdall sink: Batch verworfen ({0} Events): {1}", batch.Count, ex.Message);
        }
        return Task.CompletedTask;
    }

    public Task OnEmptyBatchAsync() => Task.CompletedTask;

    public void Dispose() => _transmitter.Dispose();
}