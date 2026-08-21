using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Heimdall.Otlp.Grpc;

/// <summary>Endpoint-Erweiterungen für den Heimdall OTLP/gRPC-Receiver.</summary>
public static class OtlpGrpcEndpointExtensions
{
    /// <summary>
    /// Mapt die drei OTLP-Collector-Services (<c>TraceService</c>/<c>LogsService</c>/
    /// <c>MetricsService</c>) an der Wurzel — bewusst OHNE Prefix, da der gRPC-Wire-Pfad
    /// durch die Proto-Package+Service festgelegt ist (<c>/opentelemetry.proto.collector.
    /// trace.v1.TraceService/Export</c>) und ein Prefix OTel-SDKs brechen würde. Der
    /// gRPC-Port ist vom UI/HTTP-Port getrennt (Kestrel HTTP/2-Endpunkt, z.B. 4317).
    /// </summary>
    public static IEndpointRouteBuilder MapHeimdallOtlpGrpc(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<TraceServiceImpl>();
        endpoints.MapGrpcService<LogsServiceImpl>();
        endpoints.MapGrpcService<MetricsServiceImpl>();
        return endpoints;
    }
}