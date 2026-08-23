using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Heimdall.AspNetCore;

/// <summary>
/// Signed-Session-Cookie-Mechanismus ohne zusätzliche Dependencies. Der Cookie
/// trägt <c>username|expiryUnix</c> als Wert plus einen HMAC-SHA256-Über die
/// beiden Felder. Das HMAC-Secret wird vom konfigurierten Passwort abgeleitet
/// (das einzige Secret, das vorhanden ist). Beim Logout wird der Cookie
/// gelöscht. Validierung in <see cref="HeimdallAuthMiddleware"/>: HMAC
/// prüfen, Expiry prüfen. Kryptografisch sauber (keine Tokens erratbar,
/// kein Replay nach Expiry).
/// </summary>
internal static class HeimdallSessionCookie
{
    /// <summary>Cookie-Wert-Format: „user|expiryUnix|hmac" (Base64Url-hex).</summary>
    private const char Sep = '|';

    /// <summary>
    /// Setzt den Session-Cookie nach erfolgreichem Login. HttpOnly (kein JS-
    /// Zugriff), SameSite=Lax (CSRF-Schutz + Top-Level-Redirect erlaubt),
    /// Secure bei HTTPS. Expiry nach <paramref name="timeoutHours"/>.
    /// </summary>
    public static void Issue(HttpResponse resp, HeimdallAuthOptions auth, string username)
    {
        var timeoutHours = auth.SessionTimeoutHours > 0 ? auth.SessionTimeoutHours : 12;
        var expiry = DateTimeOffset.UtcNow.AddHours(timeoutHours).ToUnixTimeSeconds();
        var hmac = ComputeHmac(auth.Password!, username, expiry);
        var value = $"{username}{Sep}{expiry}{Sep}{hmac}";

        var opts = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
        };
        if (resp.HttpContext.Request.IsHttps) opts.Secure = true;
        if (timeoutHours > 0) opts.MaxAge = TimeSpan.FromHours(timeoutHours);
        resp.Cookies.Append(auth.CookieName, value, opts);
    }

    /// <summary>
    /// Validiert den Session-Cookie aus dem Request. Liefert den Username
    /// (für potenzielles Audit-Logging) bei gültigem Cookie, sonst null.
    /// Prüft: vorhanden, Format, HMAC, Expiry.
    /// </summary>
    public static string? Validate(HttpRequest req, HeimdallAuthOptions auth)
    {
        if (string.IsNullOrEmpty(auth.Password)) return null;
        if (!req.Cookies.TryGetValue(auth.CookieName, out var value) || string.IsNullOrEmpty(value)) return null;
        var parts = value.Split(Sep);
        if (parts.Length != 3) return null;
        if (!long.TryParse(parts[1], out var expiry)) return null;
        if (expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        var expected = ComputeHmac(auth.Password, parts[0], expiry);
        if (!FixedTimeEquals(expected, parts[2])) return null;
        return parts[0];
    }

    /// <summary>Löscht den Session-Cookie (Logout).</summary>
    public static void Clear(HttpResponse resp, HeimdallAuthOptions auth)
        => resp.Cookies.Delete(auth.CookieName, new CookieOptions { Path = "/" });

    /// <summary>Validiert Username/Passwort gegen die Options (wie TryBasicAuth).</summary>
    public static bool CheckCredentials(string? username, string? password, HeimdallAuthOptions auth)
    {
        if (string.IsNullOrEmpty(auth.Password)) return false;
        if (string.IsNullOrEmpty(password)) return false;
        if (!SecretComparer.Equals(password, auth.Password)) return false;
        if (!string.IsNullOrEmpty(auth.Username) &&
            !SecretComparer.Equals(username?.ToLowerInvariant(), auth.Username.ToLowerInvariant())) return false;
        return true;
    }

    private static string ComputeHmac(string secret, string username, long expiry)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var payload = Encoding.UTF8.GetBytes($"{username}{Sep}{expiry}");
        var mac = HMACSHA256.HashData(key, payload);
        return Convert.ToHexString(mac);
    }

    /// <summary>Zeitkonstanter String-Vergleich (Side-Channel-Schutz).</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}