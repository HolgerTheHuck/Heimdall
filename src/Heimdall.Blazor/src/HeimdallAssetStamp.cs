using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Heimdall.Blazor;

/// <summary>
/// Cache-Buster für statische Assets: der <c>?v=</c>-Stamp folgt dem <b>Inhalt</b>
/// der Datei (SHA-256, gekürzt), nicht der Assembly-Version. Motivation: Version
/// ist konstant (1.0.0.0) und bricht keine Caches — Deployments landen lautlos auf
/// altem CSS/JS, insbesondere hinter Proxies, die Cache-Header ignorieren
/// (IIS-Output-Cache, das Panel-Freeze-Fenster). Der Content-Stamp steigt auf,
/// sobald die Datei sich ändert (auch im Dev-Loop per LastModified). Resolvierung
/// über den WebRoot-FileProvider, der in Production den Static-Web-Assets-Manifest
/// der RCL nachfahren kann (<c>_content/Heimdall.Blazor/…</c>). Kann die Datei
/// nicht aufgelöst werden (Sonderhosting/kein Provider) → Assembly-Version
/// (bestehendes Verhalten) statt leerem Stamp. Hashes werden pro (Pfad,
/// LastModified, Length) gecached — kein Re-Hash pro Request.
/// </summary>
public static class HeimdallAssetStamp
{
    private const string RclPrefix = "_content/Heimdall.Blazor/";

    private static readonly ConcurrentDictionary<string, string> Cache =
        new(StringComparer.Ordinal);

    /// <summary><c>?v=</c>-Wert für das Asset <paramref name="rel"/> (z. B. „css/heimdall.css“).</summary>
    public static string For(HttpContext? ctx, string rel)
    {
        try
        {
            if (ctx is not null)
            {
                var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
                var info = env.WebRootFileProvider.GetFileInfo(RclPrefix + rel);
                if (info.Exists && info.Length > 0)
                    return Hash(info);
            }
        }
        catch (InvalidOperationException)
        {
            // RequestServices nicht verfügbar (Test-/Render-Kontexte ohne Host).
        }
        return AssemblyVersion();
    }

    /// <summary>Fallback: Assembly-Version (wie bisher), lastet pro Release neu.</summary>
    private static string AssemblyVersion() =>
        typeof(HeimdallAssetStamp).Assembly.GetName().Version?.ToString() ?? "dev";

    private static string Hash(IFileInfo info)
    {
        var key = $"{info.PhysicalPath}|{info.LastModified.UtcTicks}|{info.Length}";
        return Cache.GetOrAdd(key, _ =>
        {
            using var stream = info.CreateReadStream();
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant(); // 16 Hex-Zeichen
        });
    }
}