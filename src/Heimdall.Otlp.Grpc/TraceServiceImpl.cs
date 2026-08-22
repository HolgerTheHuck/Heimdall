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
    private readonly OtlpAdmissionLimiter _limiter;

    /// <summary>Konstruiert den Service mit dem Ziel-Sink, Admission-Limiter und optionalen Auth-Optionen.</summary>
    public TraceServiceImpl(IHeimdallSink sink, OtlpAdmissionLimiter limiter, HeimdallOtlpGrpcOptions? opts = null)
    {
        _sink = sink;
        _limiter = limiter;
        _opts = opts;
    }

    /// <summary>Empfängt einen OTLP-Trace-Export, konvertiert nach <see cref="HSpan"/> und schreibt in den Sink.</summary>
    public override Task<ExportTraceServiceResponse> Export(
        ExportTraceServiceRequest request, ServerCallContext context)
    {
        OtlpGrpcAuth.EnsureAuthorized(context, _opts);
        // Admission-Control (C1): bei vollem Cap sofort ResourceExhausted (Retry-freundlich).
        if (!_limiter.TryEnter(out var lease))
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "otlp admission limit reached"));
        try
        {
            var spans = OtlpConvert.ToSpans(request);
            if (spans.Count > 0) _sink.WriteSpans(spans);
            return Task.FromResult(new ExportTraceServiceResponse());
        }
        finally { lease?.Dispose(); }
    }
}