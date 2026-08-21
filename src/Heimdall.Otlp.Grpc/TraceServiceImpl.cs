using System.Threading.Tasks;
using Grpc.Core;
using Heimdall;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Heimdall.Otlp.Grpc;

/// <summary>
/// gRPC-Implementierung von <c>opentelemetry.proto.collector.trace.v1.TraceService</c>.
/// Wandelt den <see cref="ExportTraceServiceRequest"/> via <c>OtlpConvert.ToSpans</c>
/// (aus Heimdall.Otlp.Proto) in <see cref="HSpan"/>-Sätze um und schreibt sie in
/// <see cref="IHeimdallSink"/>. Leere Antwort (kein Partial-Success-Reporting).
/// </summary>
public sealed class TraceServiceImpl : TraceService.TraceServiceBase
{
    private readonly IHeimdallSink _sink;
    private readonly HeimdallOtlpGrpcOptions? _opts;

    /// <summary>Konstruiert den Service mit dem Ziel-Sink und optionalen Auth-Optionen.</summary>
    public TraceServiceImpl(IHeimdallSink sink, HeimdallOtlpGrpcOptions? opts = null)
    {
        _sink = sink;
        _opts = opts;
    }

    /// <summary>Empfängt einen OTLP-Trace-Export, konvertiert nach <see cref="HSpan"/> und schreibt in den Sink.</summary>
    public override Task<ExportTraceServiceResponse> Export(
        ExportTraceServiceRequest request, ServerCallContext context)
    {
        OtlpGrpcAuth.EnsureAuthorized(context, _opts);
        var spans = OtlpConvert.ToSpans(request);
        if (spans.Count > 0) _sink.WriteSpans(spans);
        return Task.FromResult(new ExportTraceServiceResponse());
    }
}