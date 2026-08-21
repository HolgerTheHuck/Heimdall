using System.Net.Http.Headers;
using System.Text;
using Heimdall.Host;
using Microsoft.AspNetCore.Http;

namespace Heimdall.Host;

/// <summary>
/// Minimal-Auth-Middleware (host-lokal, Bibliotheken bleiben auth-frei):
/// <list type="bullet">
/// <item><see cref="HeimdallAuthOptions.Enabled"/>=false → sofort <c>_next</c> (Zero-Overhead, Demo/Embedded unverändert).</item>
/// <item>OTLP/HTTP-Pfade (<c>{Otlp.Http.Prefix}/v1/*</c>) und Prom-API-Pfade
///   (<c>{Prometheus.Prefix}/api/v1/*</c>): API-Key via Header <c>x-heimdall-key</c>
///   oder Query <c>?key=</c> == <see cref="HeimdallAuthOptions.ApiKey"/> → sonst 401.</item>
/// <item>UI / Rest: Basic-Auth gegen <see cref="HeimdallAuthOptions.UiPassword"/>
///   (Username ignoriert, Single-Shared-Password) → sonst 401 + <c>WWW-Authenticate: Basic</c>.</item>
/// </list>
/// gRPC-Auth läuft inline in den Service-Implementierungen (siehe
/// <c>OtlpGrpcAuth</c>); der Host mapt <see cref="HeimdallAuthOptions"/> →
/// <c>HeimdallOtlpGrpcOptions</c>.
/// </summary>
public sealed class HeimdallAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HeimdallHostOptions _opts;

    /// <summary>Konstruiert die Middleware mit Follow-Up und den Host-Optionen (Prefixe + Auth).</summary>
    public HeimdallAuthMiddleware(RequestDelegate next, HeimdallHostOptions opts)
    {
        _next = next;
        _opts = opts;
    }

    /// <summary>Prüft den Request-Pfad gegen die Auth-Regeln und reicht ihn ggf. weiter.</summary>
    public async Task Invoke(HttpContext context)
    {
        var auth = _opts.Auth;
        if (!auth.Enabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var otlpApi = EnsureSlash(_opts.Otlp.Http.Prefix) + "v1/";
        var promApi = EnsureSlash(_opts.Prometheus.Prefix) + "api/v1/";

        if (path.StartsWith(otlpApi, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(promApi, StringComparison.OrdinalIgnoreCase))
        {
            // API-Key-Pfade (OTLP/HTTP + Prom-API): Header oder ?key=
            var key = context.Request.Headers["x-heimdall-key"].FirstOrDefault()
                      ?? context.Request.Query["key"].FirstOrDefault();
            if (string.IsNullOrEmpty(auth.ApiKey) || key != auth.ApiKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await _next(context);
            return;
        }

        // UI / Rest: Basic-Auth (Shared-Password, Username beliebig)
        if (!TryBasicAuth(context.Request.Headers["Authorization"], auth.UiPassword))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"heimdall\"";
            return;
        }
        await _next(context);
    }

    private static string EnsureSlash(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return "/";
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    /// <summary>Prüft einen Basic-Auth-Header gegen das Shared-Password (Username ignoriert).</summary>
    private static bool TryBasicAuth(string? headerValue, string? expectedPassword)
    {
        if (string.IsNullOrEmpty(expectedPassword)) return false;
        if (string.IsNullOrEmpty(headerValue)) return false;
        if (!AuthenticationHeaderValue.TryParse(headerValue, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)) return false;
        string? password = null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter ?? string.Empty));
            var idx = decoded.IndexOf(':');
            password = idx < 0 ? decoded : decoded[(idx + 1)..];
        }
        catch { return false; }
        return password == expectedPassword;
    }
}

/// <summary>Erweiterung zum Einhängen der <see cref="HeimdallAuthMiddleware"/>.</summary>
public static class HeimdallAuthExtensions
{
    /// <summary>
    /// Hängt die Minimal-Auth-Middleware ein. Vor den <c>Map*</c>-Aufrufen registrieren.
    /// Bei <see cref="HeimdallAuthOptions.Enabled"/>=false Passthrough (Zero-Overhead).
    /// </summary>
    public static IApplicationBuilder UseHeimdallAuth(this IApplicationBuilder app, HeimdallHostOptions opts)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (opts is null) throw new ArgumentNullException(nameof(opts));
        return app.UseMiddleware<HeimdallAuthMiddleware>(opts);
    }
}