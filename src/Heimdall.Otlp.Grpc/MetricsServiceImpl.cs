using System.Threading.Tasks;
using Grpc.Core;
using Heimdall;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace Heimdall.Otlp.Grpc;

/// <summary>
/// gRPC-Implementierung von <c>opentelemetry.proto.collector.metrics.v1.MetricsService</c>.
/// Siehe <see cref="TraceServiceImpl"/> — Gegenstück für Metriken.
/// </summary>
public sealed class MetricsServiceImpl : MetricsService.MetricsServiceBase
{
    private readonly IHeimdallSink _sink;
    private readonly HeimdallOtlpGrpcOptions? _opts;
    private readonly OtlpAdmissionLimiter _limiter;

    /// <summary>Konstruiert den Service mit dem Ziel-Sink, Admission-Limiter und optionalen Auth-Optionen.</summary>
    public MetricsServiceImpl(IHeimdallSink sink, OtlpAdmissionLimiter limiter, HeimdallOtlpGrpcOptions? opts = null)
    {
        _sink = sink;
        _limiter = limiter;
        _opts = opts;
    }

    /// <summary>Empfängt einen OTLP-Metric-Export, konvertiert nach <see cref="HMetricPoint"/> und schreibt in den Sink.</summary>
    public override Task<ExportMetricsServiceResponse> Export(
        ExportMetricsServiceRequest request, ServerCallContext context)
    {
        OtlpGrpcAuth.EnsureAuthorized(context, _opts);
        if (!_limiter.TryEnter(out var lease))
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "otlp admission limit reached"));
        try
        {
            var metrics = OtlpConvert.ToMetrics(request);
            if (metrics.Count > 0) _sink.WriteMetrics(metrics);
            return Task.FromResult(new ExportMetricsServiceResponse());
        }
        finally { lease?.Dispose(); }
    }
}