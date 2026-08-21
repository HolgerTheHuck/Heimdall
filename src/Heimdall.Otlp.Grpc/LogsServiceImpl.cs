using System.Threading.Tasks;
using Grpc.Core;
using Heimdall;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace Heimdall.Otlp.Grpc;

/// <summary>
/// gRPC-Implementierung von <c>opentelemetry.proto.collector.logs.v1.LogsService</c>.
/// Siehe <see cref="TraceServiceImpl"/> — Gegenstück für Logs.
/// </summary>
public sealed class LogsServiceImpl : LogsService.LogsServiceBase
{
    private readonly IHeimdallSink _sink;
    private readonly HeimdallOtlpGrpcOptions? _opts;

    /// <summary>Konstruiert den Service mit dem Ziel-Sink und optionalen Auth-Optionen.</summary>
    public LogsServiceImpl(IHeimdallSink sink, HeimdallOtlpGrpcOptions? opts = null)
    {
        _sink = sink;
        _opts = opts;
    }

    /// <summary>Empfängt einen OTLP-Log-Export, konvertiert nach <see cref="HLogRecord"/> und schreibt in den Sink.</summary>
    public override Task<ExportLogsServiceResponse> Export(
        ExportLogsServiceRequest request, ServerCallContext context)
    {
        OtlpGrpcAuth.EnsureAuthorized(context, _opts);
        var logs = OtlpConvert.ToLogs(request);
        if (logs.Count > 0) _sink.WriteLogs(logs);
        return Task.FromResult(new ExportLogsServiceResponse());
    }
}