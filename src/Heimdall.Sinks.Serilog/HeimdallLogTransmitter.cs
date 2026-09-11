using System;
using System.Net.Http;
using System.Threading;
using Google.Protobuf;
using Heimdall;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Serilog.Core;
using SelfLog = Serilog.Debugging.SelfLog;

namespace Serilog.Sinks.Heimdall;

/// <summary>
/// Sendet einen gebauten OTLP-Logs-Request an Heimdall. Zwei Modi:
/// In-proc (direkt in den <see cref="IHeimdallSink"/>) und OTLP/HTTP
/// (POST an /v1/logs eines Heimdall-Servers). Werferfrei: Fehler werden
/// per <see cref="SelfLog"/> gemeldet, niemals in die Host-App geworfen.
/// </summary>
internal interface IHeimdallLogTransmitter : IDisposable
{
    void Transmit(ExportLogsServiceRequest request);
}

/// <summary>In-proc-Modus: OTLP-Request → OtlpConvert → IHeimdallSink.WriteLogs (kein Port/HTTP).</summary>
internal sealed class InProcLogTransmitter : IHeimdallLogTransmitter
{
    private readonly IHeimdallSink _sink;

    public InProcLogTransmitter(IHeimdallSink sink) => _sink = sink;

    public void Transmit(ExportLogsServiceRequest request)
    {
        // OtlpConvert ist die existierende OTLP→Heimdall-Konvertierung (internal,
        // per IVT): derselbe Pfad wie der OTLP-Receiver. Ein fehlerhaftes Element
        // wird dort pro Record verworfen, nicht der Batch.
        var logs = global::Heimdall.Otlp.OtlpConvert.ToLogs(request);
        if (logs.Count > 0) _sink.WriteLogs(logs);
    }

    public void Dispose() { /* Sink gehört dem Aufrufer (Heimdall.Embedded-Registrierung). */ }
}

/// <summary>
/// HTTP-Modus: POST des Protobuf-Requests (Content-Type application/x-protobuf)
/// an eine vollständige /v1/logs-URL eines Heimdall-Servers — der normale
/// OTLP-Receiver nimmt ihn entgegen wie jeden Collector-Post.
/// </summary>
internal sealed class HttpLogTransmitter : IHeimdallLogTransmitter
{
    private readonly HttpClient _client;

    public HttpLogTransmitter(string endpoint, HeimdallSinkOptions options, Action<HttpClient>? configure = null, HttpMessageHandler? handler = null)
    {
        // handler nur für Tests (Stub-Handler); belongs-to-test → disposeHandler: false.
        _client = handler is not null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _client.Timeout = options.HttpTimeout;
        if (options.Headers is not null)
        {
            foreach (var h in options.Headers) _client.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
        }
        configure?.Invoke(_client);
        Endpoint = endpoint;
    }

    /// <summary>Vollständige /v1/logs-URL (z. B. http://host:5050/otel/v1/logs) — für SelfLog-Meldungen.</summary>
    public string Endpoint { get; }

    public void Transmit(ExportLogsServiceRequest request)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        using var resp = _client.Send(msg, CancellationToken.None);
        // Nicht-2xx: Batch ist verloren — nur per SelfLog melden (werferfrei).
        if (!resp.IsSuccessStatusCode)
            SelfLog.WriteLine("Heimdall sink: OTLP-Post an {0} antwortete {1} ({2})", Endpoint, (int)resp.StatusCode, resp.StatusCode);
    }

    public void Dispose() => _client.Dispose();
}