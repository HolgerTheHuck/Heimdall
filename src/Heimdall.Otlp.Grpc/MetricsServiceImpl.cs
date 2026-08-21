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

    /// <summary>Konstruiert den Service mit dem Ziel-Sink und optionalen Auth-Optionen.</summary>
    public MetricsServiceImpl(IHeimdallSink sink, HeimdallOtlpGrpcOptions? opts = null)
    {
        _sink = sink;
        _opts = opts;
    }

    /// <summary>Empfängt einen OTLP-Metric-Export, konvertiert nach <see cref="HMetricPoint"/> und schreibt in den Sink.</summary>
    public override Task<ExportMetricsServiceResponse> Export(
        ExportMetricsServiceRequest request, ServerCallContext context)
    {
        OtlpGrpcAuth.EnsureAuthorized(context, _opts);
        var metrics = OtlpConvert.ToMetrics(request);
        if (metrics.Count > 0) _sink.WriteMetrics(metrics);
        return Task.FromResult(new ExportMetricsServiceResponse());
    }
}