using Heimdall.AspNetCore;
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
    /// Storage-Backend. 1.0 unterstützt nur „sqlite”. Das Walhalla-Backend kehrt als
    /// NuGet-Konsument zurück, sobald Heimdall.Abstractions gepackt ist. Default „sqlite“.
    /// </summary>
    public string Backend { get; set; } = "sqlite";

    /// <summary>
    /// SQLite: Dateipfad der otel.db. Default „var/heimdall/otel.db“.
    /// </summary>
    public string DataPath { get; set; } = "var/heimdall/otel.db";

    /// <summary>
    /// Retention in Tagen; 0 = unbegrenzt. Default 7. Abwärtskompat-Fallback: pro
    /// Signal gilt <see cref=”Retention”/> (falls gesetzt), sonst dieser Wert.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// Per-Signal-Retention (TTL pro Signal). null-Werte fallen auf
    /// <see cref=”RetentionDays”/> zurück; 0 = explizit unbegrenzt.
    /// </summary>
    public HeimdallRetentionOptions Retention { get; set; } = new();

    /// <summary>
    /// Harter Plafond über die gesamte DB-Datei in Bytes; 0 = unbegrenzt. Bei
    /// Überschreitung evictet der Sweeper älteste Zeilen signalübergreifend bis
    /// zum Ziel-Füllgrad (90 %). Default 0.
    /// </summary>
    public long MaxBytes { get; set; }

    /// <summary>Intervall des Retention-Sweepers (Minuten). 0 = deaktiviert. Default 30.</summary>
    public int RetentionSweepMinutes { get; set; } = 30;

    /// <summary>SQLite: WAL-Modus + foreign_keys. Default true.</summary>
    public bool WalMode { get; set; } = true;

    /// <summary>
    /// Ingest-Buffer (Bounded-Channel + Hintergrund-Batching) zwischen OTLP/SDK
    /// und dem SQLite-Sink entkoppelt Producer vom Sync-Write-Latency-Pfad. Off
    /// by default — der SQLite-Sink hat bereits Admission-Control (OTLP-Limiter)
    /// und synchrone Batch-Inserts. On = Spans/Logs/Metriken werden in einen
    /// bounded Channel geschrieben und asynchron geflushed (Drop-Policy:
    /// <see cref="Heimdall.Ingest.IngestDropPolicy.DropOldest"/> bei Overflow).
    /// Default false (1.0: synchroner Pfad ist der bewährte Default; Buffer ist
    /// Optional für High-Throughput-Szenarien, in denen der SQLite-Sink zum
    /// Bottleneck würde).
    /// </summary>
    public bool UseIngestBuffer { get; set; }

    /// <summary>
    /// auto_vacuum=INCREMENTAL beim Bootstrap + incremental_vacuum nach Sweeps
    /// (Datei schrumpft nach DELETE/Eviction). Default true.
    /// </summary>
    public bool AutoVacuum { get; set; } = true;

    /// <summary>
    /// Einmaliger VACUUM beim Start für Legacy-DBs (auto_vacuum=0, user_version=0).
    /// Teuer/exklusiv, self-gating via user_version. false = Notaus. Default true.
    /// </summary>
    public bool VacuumMigrateLegacy { get; set; } = true;

    /// <summary>
    /// Metriken-Downsampling (Rollup): rohe Metrik-Punkte werden nach
    /// <see cref="HeimdallRollupOptions.RawDays"/> Tagen zu
    /// <see cref="HeimdallRollupOptions.ResolutionSeconds"/>-Buckets aggregiert
    /// (statt hart geloescht) und bis <c>MetricsDaysEffective</c> gehalten.
    /// **Opt-In** (Default off) — bestehende Deployments unveraendert. Siehe
    /// Workstream F (ROADMAP). 1.0: eine Stufe (1 Min); mehrstufig post-1.0.
    /// </summary>
    public HeimdallRollupOptions Rollup { get; set; } = new();
}

/// <summary>
/// Per-Signal-Retention (TTL pro Signal). null = nicht gesetzt (Fallback auf
/// <see cref=”HeimdallStorageOptions.RetentionDays”/>); 0 = explizit unbegrenzt.
/// </summary>
public sealed class HeimdallRetentionOptions
{
    /// <summary>Trace-Retention in Tagen (null = Fallback, 0 = unbegrenzt).</summary>
    public int? TracesDays { get; set; }
    /// <summary>Log-Retention in Tagen (null = Fallback, 0 = unbegrenzt).</summary>
    public int? LogsDays { get; set; }
    /// <summary>Metric-Retention in Tagen (null = Fallback, 0 = unbegrenzt).</summary>
    public int? MetricsDays { get; set; }
}

/// <summary>
/// Metriken-Downsampling (Rollup) — rohe Metrik-Punkte werden nach
/// <see cref="RawDays"/> Tagen zu <see cref="ResolutionSeconds"/>-Buckets
/// aggregiert (statt hart geloescht) und bis zur gesamten Metric-Retention
/// (<c>MetricsDaysEffective</c>) gehalten. **Opt-In** (Default off). Siehe
/// Workstream F (ROADMAP). 1.0: eine Stufe (1 Min); mehrstufig post-1.0.
/// </summary>
public sealed class HeimdallRollupOptions
{
    /// <summary>Rollup aktivieren. Default false (bestehende Deployments unveraendert).</summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// Bucket-Aufloesung in Sekunden. Default 60 (1 Min — lookback-sicher fuer den
    /// fixen 5-Min-Prom-Lookback). Stufen &gt; 300 wuerden Instant-Queries luecken.
    /// </summary>
    public int ResolutionSeconds { get; set; } = 60;
    /// <summary>
    /// Tage, die rohe Metrik-Punkte unangetastet bleiben, bevor sie zu Buckets
    /// aggregiert werden. Default 1. 0 = sofort rollen. Muss &le; MetricsDaysEffective
    /// sein (sonst ist das Rollup-Fenster leer — siehe ValidateOptions).
    /// </summary>
    public int RawDays { get; set; } = 1;
}

/// <summary>OTLP-Empfänger-Konfiguration (HTTP und gRPC unabhängig schaltbar).</summary>
public sealed class HeimdallOtlpOptions
{
    /// <summary>OTLP/HTTP-Empfänger (Protobuf + JSON). Pfad C.</summary>
    public HeimdallOtlpHttpSection Http { get; set; } = new();

    /// <summary>OTLP/gRPC-Empfänger (Service-Stubs aus Heimdall.Otlp.Grpc).</summary>
    public HeimdallOtlpGrpcSection Grpc { get; set; } = new();
}

/// <summary>OTLP/HTTP-Empfänger (Host-Config-Section; Lib-Options sind <c>Heimdall.Otlp.HeimdallOtlpHttpOptions</c>).</summary>
public sealed class HeimdallOtlpHttpSection
{
    /// <summary>Empfänger aktiviert. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URL-Prefix (Wire-Pfad <c>{Prefix}/v1/{traces,metrics,logs}</c>). Default "/otel".</summary>
    public string Prefix { get; set; } = "/otel";

    /// <summary>
    /// Maximal gleichzeitige OTLP/HTTP-Export-Requests (Admission Control, C1).
    /// Weitere Requests werden sofort mit HTTP 429 abgewiesen (Retry-freundlich).
    /// <c>0</c> = unbegrenzt. Default 32 (Schutz vor Last-Spitzen / fremden Exportern).
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 32;
}

/// <summary>OTLP/gRPC-Empfänger (Host-Config-Section; Lib-Options sind <c>HeimdallOtlpGrpcOptions</c>).</summary>
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

    /// <summary>
    /// Maximal gleichzeitige OTLP/gRPC-Export-Aufrufe (Admission Control, C1); alle
    /// drei Services teilen sich das eine Cap. Weitere Aufrufe werden sofort mit
    /// <c>StatusCode.ResourceExhausted</c> abgewiesen (Retry-freundlich).
    /// <c>0</c> = unbegrenzt. Default 32.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 32;
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