using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Heimdall.Prometheus;

/// <summary>
/// Mappt die Prometheus-HTTP-API unter ein Präfix (Default <c>/otel</c>), sodass die
/// Endpunkte <c>{prefix}/api/v1/query|query_range|labels|label/{name}/values|series|
/// metadata|status/buildinfo|status/runtimeinfo|metrics</c> entstehen. Grafana zeigt
/// mit der „Prometheus"-Datenquelle auf <c>{prefix}</c> und verbindet (Buildinfo-Check).
///
/// Aufruf im Host:
/// <code>
/// app.MapHeimdallPrometheus("/otel");   // → GET /otel/api/v1/...
/// </code>
/// </summary>
public static class PromEndpointExtensions
{
    /// <summary>
    /// Mappt die Prometheus-HTTP-API (<c>/api/v1/*</c>) unter <paramref name="prefix"/>
    /// (Default <c>/otel</c>, parallel zu OTLP- und Dashboard-Mount). Löst
    /// <see cref="PromEngine"/> aus DI.
    /// </summary>
    public static IEndpointConventionBuilder MapHeimdallPrometheus(this IEndpointRouteBuilder endpoints, string prefix = "/otel")
    {
        var group = endpoints.MapGroup(prefix + "/api/v1");

        group.MapGet("/query", (PromEngine engine, HttpContext ctx) => PromHttpHandlers.Query(engine, ctx.Request));
        group.MapPost("/query", (PromEngine engine, HttpContext ctx) => PromHttpHandlers.Query(engine, ctx.Request));
        group.MapGet("/query_range", (PromEngine engine, HttpContext ctx) => PromHttpHandlers.QueryRange(engine, ctx.Request));
        group.MapGet("/labels", (PromEngine engine, HttpContext ctx) => PromHttpHandlers.Labels(engine, ctx.Request));
        group.MapGet("/label/{name}/values", (PromEngine engine, HttpContext ctx, string name) => PromHttpHandlers.LabelValues(engine, ctx.Request, name));
        group.MapGet("/series", (PromEngine engine, HttpContext ctx) => PromHttpHandlers.Series(engine, ctx.Request));
        group.MapGet("/metadata", (PromEngine engine, HttpContext ctx) => PromHttpHandlers.Metadata(engine, ctx.Request));
        group.MapGet("/status/buildinfo", () => PromHttpHandlers.BuildInfo());
        group.MapGet("/status/runtimeinfo", () => PromHttpHandlers.RuntimeInfo());
        group.MapGet("/metrics", (PromEngine engine) => PromHttpHandlers.Metrics(engine));
        return group;
    }
}