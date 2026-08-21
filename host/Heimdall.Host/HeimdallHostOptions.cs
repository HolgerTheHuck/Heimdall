using Heimdall.Blazor.Alerts;

namespace Heimdall.Host;

/// <summary>
/// Konfiguration des Stand-alone-Heimdall-Hosts. Reines POCO — gebunden via
/// <c>builder.Configuration.GetSection("Heimdall").Get&lt;HeimdallHostOptions&gt;()</c>,
/// bewusst OHNE <c>IOptions</c>-Maschinerie (konventionstreu zum Rest des Baums).
/// Siehe <c>appsettings.json</c> Sektion <c>Heimdall</c>.
/// </summary>
public sealed class HeimdallHostOptions
{
    /// <summary>Storage-Backend (1.0: SQLite), Pfade, Retention.</summary>
    public HeimdallStorageOptions Storage { get; set; } = new();

    /// <summary>OTLP-Empfänger (HTTP und/oder gRPC), abschaltbar mit Prefix.</summary>
    public HeimdallOtlpOptions Otlp { get; set; } = new();

    /// <summary>Prometheus-HTTP-API (PromQL-Engine + Exposition) für Grafana.</summary>
    public HeimdallPrometheusOptions Prometheus { get; set; } = new();

    /// <summary>Blazor-Dashboard (eingebetteter Grafana-Renderer + UI).</summary>
    public HeimdallDashboardOptions Dashboard { get; set; } = new();

    /// <summary>Dateibasierter Grafana-Dashboard-Store (persistente Dashboards).</summary>
    public HeimdallDashboardsStoreOptions DashboardsStore { get; set; } = new();

    /// <summary>Alarm-Subsystem (Regeln über Logs/Metriken/Traces, E-Mail + Webhook + Logger).</summary>
    public HeimdallAlertingOptions Alerting { get; set; } = new();

    /// <summary>Minimal-Auth: API-Key für OTLP/Prom, Basic-Auth für die UI.</summary>
    public HeimdallAuthOptions Auth { get; set; } = new();

    /// <summary>
    /// true = Demo-Daten (Spans/Logs/Metrike + MVC-Drilldown-Saat) nach dem Start seeden.
    /// Nur für Development/Demo. Persistente DB wird NICHT gelöscht (im Gegensatz zum
    /// alten SelfHost) — ein Restart erhält den Bestand.
    /// </summary>
    public bool SeedDemoData { get; set; }
}

/// <summary>Storage-Backend-Konfiguration.</summary>
public sealed class HeimdallStorageOptions
{
    /// <summary>
    /// Storage-Backend. 1.0 unterstützt nur „sqlite“. Das Walhalla-Backend kehrt als
    /// NuGet-Konsument zurück, sobald Heimdall.Abstractions gepackt ist. Default „sqlite“.
    /// </summary>
    public string Backend { get; set; } = "sqlite";

    /// <summary>
    /// SQLite: Dateipfad der otel.db. Default „var/heimdall/otel.db“.
    /// </summary>
    public string DataPath { get; set; } = "var/heimdall/otel.db";

    /// <summary>Retention in Tagen; 0 = unbegrenzt. Default 7.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Intervall des Retention-Sweepers (Minuten). Default 30.</summary>
    public int RetentionSweepMinutes { get; set; } = 30;

    /// <summary>SQLite: WAL-Modus + foreign_keys. Default true.</summary>
    public bool WalMode { get; set; } = true;
}

/// <summary>OTLP-Empfänger-Konfiguration (HTTP und gRPC unabhängig schaltbar).</summary>
public sealed class HeimdallOtlpOptions
{
    /// <summary>OTLP/HTTP-Empfänger (Protobuf + JSON). Pfad C.</summary>
    public HeimdallOtlpHttpOptions Http { get; set; } = new();

    /// <summary>OTLP/gRPC-Empfänger (Service-Stubs aus Heimdall.Otlp.Grpc).</summary>
    public HeimdallOtlpGrpcSection Grpc { get; set; } = new();
}

/// <summary>OTLP/HTTP-Empfänger.</summary>
public sealed class HeimdallOtlpHttpOptions
{
    /// <summary>Empfänger aktiviert. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URL-Prefix (Wire-Pfad <c>{Prefix}/v1/{traces,metrics,logs}</c>). Default "/otel".</summary>
    public string Prefix { get; set; } = "/otel";
}

/// <summary>OTLP/gRPC-Empfänger.</summary>
public sealed class HeimdallOtlpGrpcSection
{
    /// <summary>gRPC-Empfänger aktiviert (benötigt HTTP/2-Endpunkt). Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Bindungs-URL des HTTP/2-Endpunkts (nur Doku/Referenz — die echte Bindung erfolgt
    /// über <c>Kestrel:Endpoints</c> in appsettings, da nur dort pro-Endpunkt-Protokolle
    /// (Http2) einstellbar sind). Default "http://localhost:4317".
    /// </summary>
    public string Url { get; set; } = "http://localhost:4317";
}

/// <summary>Prometheus-HTTP-API.</summary>
public sealed class HeimdallPrometheusOptions
{
    /// <summary>Prom-API aktiviert (PromQL-Engine + Text-Exposition). Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URL-Prefix (Wire-Pfad <c>{Prefix}/api/v1/...</c>). Default "/otel".</summary>
    public string Prefix { get; set; } = "/otel";
}

/// <summary>Blazor-Dashboard.</summary>
public sealed class HeimdallDashboardOptions
{
    /// <summary>Dashboard aktiviert. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URL-Prefix. Default "/otel".</summary>
    public string Prefix { get; set; } = "/otel";
}

/// <summary>Dateibasierter Dashboard-Store.</summary>
public sealed class HeimdallDashboardsStoreOptions
{
    /// <summary>Verzeichnis für die Grafana-Dashboard-JSONs. Default "var/heimdall/dashboards".</summary>
    public string Dir { get; set; } = "var/heimdall/dashboards";

    /// <summary>true = heimdall-overview.json beim Start ablegen, falls nicht vorhanden. Default false.</summary>
    public bool SeedExample { get; set; }
}

/// <summary>Minimal-Auth-Konfiguration.</summary>
public sealed class HeimdallAuthOptions
{
    /// <summary>Auth aktiviert. false (Default) = Zero-Overhead-Passthrough (Demo/Embedded unverändert).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Shared API-Key für OTLP/HTTP- und Prom-API-Pfade (Header <c>x-heimdall-key</c> oder
    /// Query <c>?key=</c>). Erforderlich, wenn <see cref="Enabled"/> true. OTel-SDKs setzen ihn
    /// via <c>OTEL_EXPORTER_OTLP_HEADERS="x-heimdall-key=…"</c>; Grafana als Custom-Header.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Shared Passwort für die UI (Basic-Auth, Username ignoriert). null bei <see cref="Enabled"/>=false.
    /// </summary>
    public string? UiPassword { get; set; }
}