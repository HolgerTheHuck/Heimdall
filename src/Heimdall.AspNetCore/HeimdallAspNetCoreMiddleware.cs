using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace Heimdall.AspNetCore;

/// <summary>
/// Dünnes Enrichment-Middleware: nachdem Routing den Endpunkt ausgewählt hat (also
/// nach <c>UseRouting()</c>), liest sie die MVC-Metadaten des Endpunkts und taggt
/// den laufenden <see cref="Activity"/> (den OTel-Server-Span, den die
/// ASP.NET-Core-OTel-Instrumentation erzeugt hat) mit
/// <list type="bullet">
///   <item><c>aspnetmvc.controller</c></item>
///   <item><c>aspnetmvc.action</c></item>
///   <item><c>aspnetmvc.route</c> (Routen-Template)</item>
/// </list>
/// Diese Tags wandern über den Heimdall.Sdk-Exporter (oder OTLP) als Span-Attribute
/// in den Storage und erlauben dem Heimdall-Dashboard den Drilldown
/// API → Controller → Endpoint nach echten Namen statt per Route-Template-Parsen.
/// <para>
/// Die Middleware misst nichts selbst und erzeugt keine eigene Metrik — sie hängt
/// nur Tags an den bestehenden Server-Span. Fehlt ein <see cref="Activity.Current"/>
/// (z. B. OTel-Instrumentation nicht aktiv) oder kein Endpunkt, ist sie no-op.
/// </para>
/// <para>
/// Einbindung im Host: <c>app.UseRouting(); app.UseHeimdallAspNetCore();
/// app.MapControllers();</c> — nach <c>UseRouting</c> (damit <c>GetEndpoint()</c>
/// gesetzt ist), vor <c>MapControllers</c> (damit die Tags vorhanden sind, bevor die
/// Action läuft und ggf. weitere Spans startet).
/// </para>
/// </summary>
public sealed class HeimdallAspNetCoreMiddleware
{
    public const string ControllerTag = "aspnetmvc.controller";
    public const string ActionTag = "aspnetmvc.action";
    public const string RouteTag = "aspnetmvc.route";

    private readonly RequestDelegate _next;

    public HeimdallAspNetCoreMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        Enrich(context);
        await _next(context);
    }

    private static void Enrich(HttpContext context)
    {
        var activity = Activity.Current;
        if (activity is null) return;                // keine OTel-Instrumentation → no-op

        var endpoint = context.GetEndpoint();
        if (endpoint is null) return;                // Routing hat nichts gematcht → no-op

        // Primärquelle: ControllerActionDescriptor (hat verlässlich Controller/Action).
        string? controller = null;
        string? action = null;
        var cad = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (cad is not null)
        {
            controller = cad.ControllerName;
            action = cad.ActionName;
        }

        // Fallback: Route-Values (für minimal-API / nicht-Controller-Endpunkte, die
        // trotzdem {controller}/{action} gebunden haben).
        var routeValues = context.Request.RouteValues;
        if (routeValues is not null)
        {
            controller ??= routeValues["controller"]?.ToString();
            action ??= routeValues["action"]?.ToString();
        }

        // Route-Template des gematchten Endpunkts.
        string? route = (endpoint as RouteEndpoint)?.RoutePattern?.RawText;

        if (!string.IsNullOrEmpty(controller)) activity.SetTag(ControllerTag, controller);
        if (!string.IsNullOrEmpty(action)) activity.SetTag(ActionTag, action);
        if (!string.IsNullOrEmpty(route)) activity.SetTag(RouteTag, route);
    }
}