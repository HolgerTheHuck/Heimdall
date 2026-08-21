using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.AspNetCore;

/// <summary>
/// Erweiterungen zum Einbinden des Heimdall-ASP.NET-Core-Enrichments.
/// </summary>
public static class HeimdallAspNetCoreExtensions
{
    /// <summary>
    /// Registriert das Heimdall-ASP.NET-Core-Enrichment. Aktuell no-op (die Middleware
    /// ist zustandslos); vorhanden, damit die Registrierungs-Oberfläche stabil ist,
    /// falls später Optionen dazukommen.
    /// </summary>
    public static IServiceCollection AddHeimdallAspNetCore(this IServiceCollection services)
        => services;

    /// <summary>
    /// Hängt die <see cref="HeimdallAspNetCoreMiddleware"/> in die Pipeline ein.
    /// Aufruf <b>nach</b> <c>UseRouting()</c> und <b>vor</b> <c>MapControllers()</c>,
    /// damit der gematchte Endpunkt (<c>HttpContext.GetEndpoint()</c>) bereits gesetzt
    /// ist und <see cref="Activity.Current"/> der OTel-Server-Span ist.
    /// </summary>
    public static IApplicationBuilder UseHeimdallAspNetCore(this IApplicationBuilder app)
        => app.UseMiddleware<HeimdallAspNetCoreMiddleware>();
}