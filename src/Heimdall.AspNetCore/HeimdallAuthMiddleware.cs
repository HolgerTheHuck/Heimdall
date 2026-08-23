using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Heimdall;                       // SecretComparer (Heimdall.Abstractions)
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.AspNetCore;

/// <summary>
/// Minimal-Auth-Middleware (Bibliothek, nutzbar durch Stand-alone-Host UND
/// eingebettete Apps). Opt-in über <see cref="HeimdallAuthOptions.Enabled"/>;
/// bei false sofort <c>_next</c> (Zero-Overhead).
/// <list type="bullet">
/// <item><see cref="HeimdallAuthOptions.ProtectedPrefix"/> gesetzt → nur Pfade
///   unter diesem Prefix werden geprüft; alles andere passiert unverändert
///   (Embedded: App-eigene Routes <c>/api/…</c> bleiben frei). null = global
///   (Host-Verhalten, dessen Routes sämtlich Heimdalls sind).</item>
/// <item>OTLP/HTTP-Pfade (<c>{OtlpHttpPrefix}/v1/*</c>) und Prom-API-Pfade
///   (<c>{PrometheusPrefix}/api/v1/*</c>): API-Key via Header
///   <c>x-heimdall-key</c> (Header only — kein Query-Fallback, da Query-Strings
///   in Access-Logs landen); zeitkonstanter Vergleich (<see cref="SecretComparer"/>)
///   gegen <see cref="HeimdallAuthOptions.ApiKey"/> → sonst 401.</item>
/// <item>UI / Rest (innerhalb des geschützten Subtree): Basic-Auth gegen
///   <see cref="HeimdallAuthOptions.Username"/> + <see cref="HeimdallAuthOptions.Password"/>
///   (Username nur geprüft, wenn konfiguriert) → sonst 401 +
///   <c>WWW-Authenticate: Basic</c>.</item>
/// </list>
/// </summary>
public sealed class HeimdallAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HeimdallAuthOptions _auth;

    /// <summary>Konstruiert die Middleware mit Follow-Up und den Auth-Optionen.</summary>
    public HeimdallAuthMiddleware(RequestDelegate next, HeimdallAuthOptions auth)
    {
        _next = next;
        _auth = auth;
    }

    /// <summary>Prüft den Request-Pfad gegen die Auth-Regeln und reicht ihn ggf. weiter.</summary>
    public async Task Invoke(HttpContext context)
    {
        if (!_auth.Enabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var req = context.Request;

        // Login/Logout-Endpoints selbst nie auth-gate (sonst Endlosschleife).
        if (path == _auth.LoginPath || path == _auth.LogoutPath)
        {
            await _next(context);
            return;
        }

        // ProtectedPrefix gesetzt → nur dieser Subtree wird geschützt (Embedded).
        // null = global (Host). Vergleich wie die API-Pfad-Prüfung unten (OID).
        var prefix = _auth.ProtectedPrefix;
        if (!string.IsNullOrEmpty(prefix) &&
            !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var otlpApi = EnsureSlash(_auth.OtlpHttpPrefix) + "v1/";
        var promApi = EnsureSlash(_auth.PrometheusPrefix) + "api/v1/";

        // API-Pfade (OTLP/HTTP + Prom-API): API-Key via Header (kein Cookie,
        // kein Redirect — API-Clients folgen keinen Redirects). 401 bei fehlendem/
        // ungültigem Key.
        if (path.StartsWith(otlpApi, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(promApi, StringComparison.OrdinalIgnoreCase))
        {
            var key = req.Headers["x-heimdall-key"].FirstOrDefault();
            if (string.IsNullOrEmpty(_auth.ApiKey) || !SecretComparer.Equals(key, _auth.ApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await _next(context);
            return;
        }

        // UI / Rest: Session-Cookie prüfen. Fehlt/ungültig → Redirect auf
        // Login-Seite (mit returnUrl für Rückkehr nach erfolgreichem Login).
        // Basic-Auth-Header als Fallback (für API-Automatisierung/Scripting).
        if (HeimdallSessionCookie.Validate(req, _auth) is not null)
        {
            await _next(context);
            return;
        }
        // Basic-Auth-Fallback (für Scripting/Curl, nicht Browser).
        if (TryBasicAuth(req.Headers["Authorization"], _auth))
        {
            await _next(context);
            return;
        }

        // Browser-Nutzer: Redirect auf Login-Seite.
        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
            req.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            var returnUrl = path + req.QueryString.Value;
            var loginUrl = _auth.LoginPath + "?returnUrl=" + Uri.EscapeDataString(returnUrl);
            context.Response.Redirect(loginUrl);
            return;
        }
        // Non-GET ohne Auth (z. B. POST ohne Cookie): 401 statt Redirect
        // (POST-Clients folgen keinen Redirects sinnvoll).
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    private static string EnsureSlash(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return "/";
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    /// <summary>
    /// Prüft einen Basic-Auth-Header gegen Username (falls konfiguriert) + Passwort.
    /// Username null = beliebiger User (Shared-Password, abwärtskompatibel). Passwort
    /// stets case-sensitiv; Username case-insensitiv („Admin" == „admin" — Usernamen
    /// merkt man sich ohne exakte Groß-/Kleinschreibung, Passwörter nicht). Beide
    /// Vergleiche zeitkonstant (<see cref="SecretComparer"/> auf den lower-cased
    /// UTF-8-Bytes beim Username).
    /// </summary>
    private static bool TryBasicAuth(string? headerValue, HeimdallAuthOptions auth)
    {
        if (string.IsNullOrEmpty(auth.Password)) return false;
        if (string.IsNullOrEmpty(headerValue)) return false;
        if (!AuthenticationHeaderValue.TryParse(headerValue, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)) return false;

        string? user = null;
        string? pass = null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter ?? string.Empty));
            var idx = decoded.IndexOf(':');
            if (idx < 0) pass = decoded;
            else { user = decoded[..idx]; pass = decoded[(idx + 1)..]; }
        }
        catch { return false; }

        // Passwort case-sensitiv (Secret ist ein Secret).
        if (!SecretComparer.Equals(pass, auth.Password)) return false;
        // Username case-insensitiv: beide Seiten lower-cased, dann zeitkonstant
        // vergleichen. „Admin"/„ADMIN" treffen ebenso wie „admin".
        if (!string.IsNullOrEmpty(auth.Username) &&
            !SecretComparer.Equals(user?.ToLowerInvariant(), auth.Username.ToLowerInvariant())) return false;
        return true;
    }
}

/// <summary>Erweiterung zum Einhängen der <see cref="HeimdallAuthMiddleware"/>.</summary>
public static class HeimdallAuthExtensions
{
    /// <summary>
    /// Hängt die Minimal-Auth-Middleware ein. Vor den <c>Map*</c>-Aufrufen
    /// registrieren. Bei <see cref="HeimdallAuthOptions.Enabled"/>=false Passthrough
    /// (Zero-Overhead). Registriert die Options zusätzlich als Singleton in DI
    /// (falls noch nicht registriert), sodass der Login/Logout-Handler (in der
    /// Blazor-Schicht) sie auslösen kann.
    /// </summary>
    /// <summary>
    /// Hängt die Minimal-Auth-Middleware ein. Vor den <c>Map*</c>-Aufrufen
    /// registrieren. Bei <see cref="HeimdallAuthOptions.Enabled"/>=false Passthrough
    /// (Zero-Overhead).
    ///
    /// <b>Hinweis:</b> Damit der Login/Logout-Handler (in der Blazor-Schicht)
    /// die Options aus DI auslösen kann, vorher
    /// <see cref="AddHeimdallAuth"/> auf der Service-Collection aufrufen:
    /// <code>
    /// builder.Services.AddHeimdallAuth(opts);
    /// app.UseHeimdallAuth(opts);
    /// </code>
    /// </summary>
    public static IApplicationBuilder UseHeimdallAuth(this IApplicationBuilder app, HeimdallAuthOptions auth)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (auth is null) throw new ArgumentNullException(nameof(auth));
        return app.UseMiddleware<HeimdallAuthMiddleware>(auth);
    }

    /// <summary>
    /// Registriert die <see cref="HeimdallAuthOptions"/> als Singleton in der DI,
    /// sodass der Login/Logout-Handler (in der Blazor-Schicht) sie auslösen kann.
    /// Alternativ zu UseHeimdallAuth — oder ergänzend, wenn die Options bereits
    /// via UseHeimdallAuth übergeben wurden (dann redundant, aber idempotent).
    /// </summary>
    public static IServiceCollection AddHeimdallAuth(this IServiceCollection services, HeimdallAuthOptions auth)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (auth is null) throw new ArgumentNullException(nameof(auth));
        services.AddSingleton(auth);
        return services;
    }
}