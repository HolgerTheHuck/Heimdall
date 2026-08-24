namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Alerting-Konfiguration. Plain `sealed class`-POCO (KEIN IOptions —
// Konvention des Hosts: Bindung via GetSection + manuelle ValidateOptions).
// Secrets (SMTP-Password, Webhook-Headers) koennen via Env-Varen
// (`Heimdall__Alerting__Smtp__Password` …) ueberschrieben werden.
// ---------------------------------------------------------------------------

/// <summary>SMTP-Kanal-Konfiguration (System.Net.Mail, Framework — kein NuGet).</summary>
public sealed class SmtpChannelOptions
{
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public bool UseTls { get; set; } = true;
}

/// <summary>Webhook-Kanal-Konfiguration (POST JSON — deckt Slack/Teams/PagerDuty ab).</summary>
public sealed class WebhookChannelOptions
{
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Alerting-Subsystem-Optionen. Der Host bindet diese aus
/// <c>Heimdall:Alerting</c>; Samples koennen sie per Code uebergeben.
/// </summary>
public sealed class HeimdallAlertingOptions
{
    /// <summary>Aktiviert den periodischen AlertEvaluator + registriert Kanäle.false (default) = nur Store/UI, keine Auswertung.</summary>
    public bool Enabled { get; set; }

    /// <summary>Globaler Auswerte-Takt in Sekunden (Regeln mit EvalIntervalSeconds=0 nehmen diesen).</summary>
    public long EvaluationIntervalSeconds { get; set; } = 15;

    /// <summary>Verzeichnis fuer Regel-JSONs ({dir}/{id}.json).</summary>
    public string RulesDir { get; set; } = "var/heimdall/alerts/rules";

    /// <summary>Verzeichnis fuer den Zustands-Store (alertstate.json).</summary>
    public string StateDir { get; set; } = "var/heimdall/alerts";

    public SmtpChannelOptions Smtp { get; set; } = new();

    public WebhookChannelOptions Webhook { get; set; } = new();

    /// <summary>
    /// Sprache für asynchrone Alert-Benachrichtigungen (Mail-Body/-Betreff), da der
    /// AlertEvaluator als Singleton-HostedService außerhalb eines HTTP-Kontexts läuft
    /// und das pro-Request `heimdall-lang`-Cookie nicht lesen kann. "de"|"en"|"fr";
    /// Default "de". Webhook-Payloads bleiben maschinen-lesbar (unübersetzt).
    /// </summary>
    public string Language { get; set; } = "de";

    /// <summary>Logger-Kanal (Konsol-Log) registrieren — immer verfuegbar, gut fuer Dev/Demo.</summary>
    public bool LoggerEnabled { get; set; } = true;
}