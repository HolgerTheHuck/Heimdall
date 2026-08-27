using Microsoft.AspNetCore.Http;

namespace Heimdall.Blazor;

/// <summary>
/// Pfad-Basis-Support für Deployment hinter einem Pfad-Prefix (IIS-Unterverzeichnis/
/// Sub-Application, Reverse-Proxy mit Pfad-Strip): ANCM/der Proxy legt das externe
/// Verzeichnis in <c>Request.PathBase</c> ab — die App sieht intern nur den Rest.
/// Alle generierten UI-Links (Nav, Login-Redirects, Root-Redirect) müssen daher
/// <see cref="FullPrefix"/> verwenden statt des rohen konfigurierten Prefix, sonst
/// landen Links am Domain-Root (404/Asset-Verlust) bzw. die Root-Weiterleitung loopt.
/// Asset-URLs (<c>/_content/…</c>) brauchen nur die PathBase selbst — sie liegen am
/// App-Root, nicht unter dem Dashboard-Prefix (siehe <see cref="AssetBase"/>).
/// PathBase leer (Site-Root-Deployment) → Rückgabe unverändert (altes Verhalten).
/// </summary>
public static class HeimdallUiPaths
{
    /// <summary>
    /// Externer Prefix = <c>Request.PathBase + prefix</c>. Wird als
    /// <c>BasePath</c> in die Seiten durchgereicht, sodass <c>{BasePath}/traces</c>
    /// usw. im Browser das Unterverzeichnis mitschleppen.
    /// </summary>
    public static string FullPrefix(HttpContext? ctx, string prefix)
    {
        var pb = BaseSegment(ctx);
        if (pb.Length == 0) return prefix;
        return pb + (prefix.StartsWith('/') ? prefix : "/" + prefix);
    }

    /// <summary>
    /// Basis für App-root-relative Asset-URLs (<c>/_content/…</c>): nur die
    /// PathBase, ohne Dashboard-Prefix. <c>/_content/Heimdall.Blazor/css/…</c>
    /// liegt am App-Root — extern also <c>{PathBase}/_content/…</c>.
    /// </summary>
    public static string AssetBase(HttpContext? ctx) => BaseSegment(ctx);

    private static string BaseSegment(HttpContext? ctx)
    {
        var pb = ctx?.Request.PathBase.Value ?? string.Empty;
        return pb.Length <= 1 ? string.Empty : pb.TrimEnd('/');
    }
}