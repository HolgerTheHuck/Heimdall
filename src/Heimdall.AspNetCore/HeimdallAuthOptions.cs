using System;

namespace Heimdall.AspNetCore;

/// <summary>
/// Minimal-Auth-Konfiguration für die Heimdall-Oberfläche (UI + OTLP/HTTP- +
/// Prom-API-Pfade). Opt-in: <see cref="Enabled"/>=false (Default) =
/// Zero-Overhead-Passthrough. POCO — gebunden via
/// <c>Configuration.GetSection("Heimdall:Auth").Get&lt;HeimdallAuthOptions&gt;()</c>.
/// </summary>
/// <remarks>
/// Lebte früher host-lokal in <c>Heimdall.Host</c> (als <c>UiPassword</c>-only
/// Shared-Password, Username ignoriert). Jetzt in der <c>Heimdall.AspNetCore</c>-
/// Bibliothek, sodass Stand-alone-Host UND eingebettete Apps dieselbe Auth nutzen.
/// <see cref="Username"/> ist additiv: null = beliebiger Username (altes
/// Shared-Password-Verhalten); gesetzt = muss zusätzlich zeitkonstant passen
/// (<see cref="Heimdall.SecretComparer"/>). <see cref="ProtectedPrefix"/> null =
/// global schützen (Host); gesetzt = nur dieser Subtree (Embedded, damit
/// App-eigenen Routes frei bleiben).
/// </remarks>
public sealed class HeimdallAuthOptions
{
    /// <summary>Auth aktiviert. false (Default) = Passthrough (Zero-Overhead).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Username für die UI-Basic-Auth. null = beliebiger Username (Shared-Password,
    /// abwärtskompatibel zum alten Host-Verhalten); gesetzt = muss zusätzlich zum
    /// <see cref="Password"/> zeitkonstant passen. Vergleich über
    /// <see cref="Heimdall.SecretComparer"/>.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Passwort für die UI (Basic-Auth). Erforderlich, wenn <see cref="Enabled"/>
    /// true (siehe <see cref="Validate"/>). Vergleich zeitkonstant.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Shared API-Key für OTLP/HTTP- und Prom-API-Pfade (Header
    /// <c>x-heimdall-key</c> — Header only, kein Query-Fallback, da Query-Strings
    /// in Access-Logs landen). Vergleich zeitkonstant. null/leer bei
    /// <see cref="Enabled"/>=true → API-Pfade liefern 401 (sicherer Default);
    /// der Host fordert ApiKey zusätzlich ein (siehe Host-ValidateOptions).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Nur Pfade unter diesem Prefix werden geschützt; alles andere passiert
    /// unverändert (<c>_next</c>). null (Default) = global schützen (Stand-alone-
    /// Host, dessen Routes sämtlich Heimdalls sind). Für Embedded auf z. B.
    /// <c>"/otel"</c> setzen, damit die App-eigenen Routes (<c>/api/…</c>)
    /// frei bleiben und nur die Heimdall-Oberfläche hinter dem Login steht.
    /// </summary>
    public string? ProtectedPrefix { get; set; }

    /// <summary>URL-Prefix der OTLP/HTTP-API (Wire-Pfad <c>{Prefix}/v1/*</c>). Default "/otel".</summary>
    public string OtlpHttpPrefix { get; set; } = "/otel";

    /// <summary>URL-Prefix der Prom-API (Wire-Pfad <c>{Prefix}/api/v1/*</c>). Default "/otel".</summary>
    public string PrometheusPrefix { get; set; } = "/otel";

    /// <summary>
    /// Session-Cookie-Gültigkeit in Stunden. Default 12. Der Cookie wird beim
    /// Login gesetzt (signed mit HMAC über das Passwort) und nach Ablauf von
    /// der Middleware abgewiesen → Login-Seite. 0 = Session-Cookie (bis Browser
    /// geschlossen). Funktioniert nur, wenn <see cref="Enabled"/>=true.
    /// </summary>
    public int SessionTimeoutHours { get; set; } = 12;

    /// <summary>
    /// Name des Session-Cookies. Default „heimdall-auth". Wird beim Login
    /// gesetzt (HttpOnly, SameSite=Lax, Secure bei HTTPS) und bei /logout
    /// gelöscht.
    /// </summary>
    public string CookieName { get; set; } = "heimdall-auth";

    /// <summary>
    /// Pfad der Login-Seite (relativ zur App-Root). Default „/login". Die
    /// Middleware redirectet bei fehlendem/ungültigem Cookie auf diesen Pfad
    /// (mit <c>?returnUrl=</c>-Parameter). Die Blazor-Schicht mappt diese
    /// Route auf die <c>LoginPage.razor</c> (via <c>MapHeimdallDashboard</c>).
    /// </summary>
    public string LoginPath { get; set; } = "/login";

    /// <summary>
    /// Pfad des Logout-Endpoints (POST). Default „/logout". Löscht den
    /// Session-Cookie und redirectet auf <see cref="LoginPath"/>.
    /// </summary>
    public string LogoutPath { get; set; } = "/logout";

    /// <summary>
    /// Pfade, die Auth völlig umgehen (anonymous), selbst bei
    /// <see cref="Enabled"/>=true und innerhalb von <see cref="ProtectedPrefix"/>.
    /// Gedacht für Health-/Readiness-Probes (z. B. <c>/healthz</c>), die von
    /// Compose/Kubernetes ohne Credentials gerufen werden und ein 200/302-
    /// Redirect auf die Login-Seite fälschlich als „unhealthy" werten würden.
    /// Exakter Pfad-Vergleich (case-sensitive, wie <see cref="LoginPath"/>);
    /// null/leer (Default) = keine Ausnahmen. Der Host trägt hier
    /// <c>/healthz</c> ein; Embedded-Nutzer überlassen es der App-eigenen
    /// Health-Route (die via <see cref="ProtectedPrefix"/> ohnehin frei ist).
    /// </summary>
    public string[]? AnonymousPaths { get; set; }

    /// <summary>
    /// Pfad-Prefixe, die Auth völlig umgehen (anonymous), auch bei
    /// <see cref="Enabled"/>=true — im Gegensatz zu <see cref="AnonymousPaths"/>
    /// als <c>StartsWith</c>-Match auf dem in-app Pfad (ohne PathBase), inkl.
    /// abschließendem Slash. Gedacht für statische Web-Assets, die die
    /// Login-Seite selbst benötigt: das Stylesheet
    /// <c>/_content/Heimdall.Blazor/css/heimdall.css</c> lädt beim Redirect auf
    /// den Login-Screen gerade <b>ohne</b> Session-Cookie — steht es hinter der
    /// Auth, rendert die Login-Seite unstyled. Der Host trägt hier
    /// <c>/_content/Heimdall.Blazor/</c> ein (nur Heimdalls eigene Assets —
    /// keine app-fremden <c>/_content</c>-Pfade werden geöffnet).
    /// null/leer (Default) = keine Prefix-Ausnahmen.
    /// </summary>
    public string[]? AnonymousPrefixes { get; set; }

    /// <summary>
    /// Validiert die Baseline: <see cref="Enabled"/>=true erfordert ein nicht-leeres
    /// <see cref="Password"/>. Wirft andernfalls <see cref="InvalidOperationException"/>.
    /// Host-spezifische Zusatz-Validierung (ApiKey-Pflicht) bleibt dem Host überlassen.
    /// </summary>
    public void Validate()
    {
        if (Enabled && string.IsNullOrEmpty(Password))
            throw new InvalidOperationException(
                "Heimdall:Auth:Enabled=true erfordert Password (Basic-Auth).");
    }
}