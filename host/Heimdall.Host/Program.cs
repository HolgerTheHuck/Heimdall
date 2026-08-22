using Heimdall;
using Heimdall.AspNetCore;
using Heimdall.Blazor;
using Heimdall.Direct;
using Heimdall.Host;
using Heimdall.Otlp;
using Heimdall.Otlp.Grpc;
using Heimdall.Prometheus;
using Heimdall.Storage.SQLite;

// --- Heimdall Stand-alone-Host ----------------------------------------------
// Config-getriebenes, persistentes Backend (Grafana-Stack-Äquivalent): Dashboard (Blazor),
// OTLP-Empfänger (HTTP + gRPC), Prometheus-API, dateibasierter Dashboard-Store — alles
// schaltbar via appsettings Sektion "Heimdall". Embedded-Nutzung bleibt unangetastet
// (alle Add*/Map*-Signaturen unverändert, keine IOptions-Maschinerie).
//
// Start:  `dotnet run --project host/Heimdall.Host`  →  http://localhost:5099/otel
// OTLP/HTTP:  POST /otel/v1/{traces,metrics,logs}   (Protobuf + JSON)
// OTLP/gRPC:  localhost:4317  (opentelemetry.proto.collector.{trace,logs,metrics}.v1)
// Prom-API:   GET  /otel/api/v1/{query,query_range,labels,...}  (Grafana-Datenquelle)
// Docker (SQLite-only):  docker build -t heimdall -f host/Heimdall.Host/Dockerfile .

var builder = WebApplication.CreateBuilder(args);

// POCO-Bindung (bewusst ohne IOptions): Sektion "Heimdall" → HeimdallHostOptions.
var opts = builder.Configuration.GetSection("Heimdall").Get<HeimdallHostOptions>() ?? new HeimdallHostOptions();
ValidateOptions(opts);

// Persistente Verzeichnisse anlegen — die DB-Datei wird NICHT gelöscht (Gegensatz zum alten SelfHost).
var dataDir = Path.GetDirectoryName(Path.GetFullPath(opts.Storage.DataPath));
if (!string.IsNullOrEmpty(dataDir)) Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(opts.DashboardsStore.Dir);
Directory.CreateDirectory(opts.Alerting.RulesDir);
Directory.CreateDirectory(opts.Alerting.StateDir);

// Backend-Sink bauen (1.0: SQLite). Der Sink implementiert IHeimdallSink,
// IHeimdallQuery UND IHeimdallMetricSource — dasselbe Objekt geht in alle Add*-Aufrufe.
var (sink, query, metricSource, sinkDisposable) = BuildSink(opts);

// --- Bedingte DI-Registrierung ----------------------------------------------
if (opts.Dashboard.Enabled) builder.Services.AddHeimdallDashboard(query);
if (opts.Otlp.Http.Enabled)
    builder.Services.AddHeimdallOtlp(sink, new Heimdall.Otlp.HeimdallOtlpHttpOptions
    { MaxConcurrentRequests = opts.Otlp.Http.MaxConcurrentRequests });
if (opts.Otlp.Grpc.Enabled)
{
    // gRPC-Auth + Admission-Control: Host mapt Auth → HeimdallOtlpGrpcOptions (inline-Check in den *ServiceImpl).
    var grpcOpts = new HeimdallOtlpGrpcOptions { MaxConcurrentRequests = opts.Otlp.Grpc.MaxConcurrentRequests };
    if (opts.Auth.Enabled) { grpcOpts.AuthEnabled = true; grpcOpts.ApiKey = opts.Auth.ApiKey; }
    builder.Services.AddHeimdallOtlpGrpc(sink, grpcOpts);
}
if (opts.Prometheus.Enabled) builder.Services.AddHeimdallPrometheus(metricSource, query);
builder.Services.AddHeimdallDashboards(opts.DashboardsStore.Dir);
// Alarm-Subsystem: Store/UI immer (Route /otel/alerts funktioniert ohne aktiven Evaluator);
// Evaluator + Kanäle nur wenn Alerting.Enabled.
builder.Services.AddHeimdallAlerting(query, opts.Alerting);
builder.Services.AddSingleton(opts);   // HostShutdownTest + ggf. weitere DI-Konsumenten

var app = builder.Build();

// Beispiel-Dashboard seeden (idempotent), falls konfiguriert — vor Auth, nach Build.
if (opts.DashboardsStore.SeedExample) HeimdallSeeder.SeedExampleDashboard(app.Services);

// Auth vor den Map*-Aufrufen einhängen (Passthrough bei Enabled=false). Die
// gehobene Lib-Middleware liest die API-Prefixe aus den Auth-Optionen (früher
// direkt aus HeimdallHostOptions) → hier syncen. ProtectedPrefix bleibt null
// (Host = global, dessen Routes sämtlich Heimdalls sind).
opts.Auth.OtlpHttpPrefix = opts.Otlp.Http.Prefix;
opts.Auth.PrometheusPrefix = opts.Prometheus.Prefix;
if (opts.Auth.Enabled) app.UseHeimdallAuth(opts.Auth);

app.UseStaticFiles();   // liefert /_content/Heimdall.Blazor/{css,js}

// --- Bedingte Endpoint-Mappings --------------------------------------------
if (opts.Dashboard.Enabled) app.MapHeimdallDashboard(opts.Dashboard.Prefix);
if (opts.Otlp.Http.Enabled) app.MapHeimdallOtlp(opts.Otlp.Http.Prefix);
if (opts.Prometheus.Enabled) app.MapHeimdallPrometheus(opts.Prometheus.Prefix);
if (opts.Otlp.Grpc.Enabled) app.MapHeimdallOtlpGrpc();   // gRPC-Wire-Path ist proto-fixiert (kein Prefix)
app.MapGet("/", () => Results.Redirect(opts.Dashboard.Enabled ? opts.Dashboard.Prefix : "/otel", permanent: false));

// Demo-Daten rein additiv seeden (DB bleibt erhalten).
if (opts.SeedDemoData) HeimdallSeeder.SeedDemoData(sink);
if (opts.SeedDemoData) HeimdallSeeder.SeedDemoAlerts(app.Services);

// Sauberer Shutdown (C4): Sink disposen, NACH Kestrel-Drain (ApplicationStopped),
// damit in-flight OTLP-Writes committen, bevor der SQLite-Sink/_conn weg ist.
app.Lifetime.ApplicationStopped.Register(() => sinkDisposable.Dispose());

app.Run();

// --- Optionen-Validierung ----------------------------------------------------
static void ValidateOptions(HeimdallHostOptions o)
{
    var backend = o.Storage.Backend?.Trim().ToLowerInvariant();
    if (backend != "sqlite")
        throw new InvalidOperationException(
            $"Heimdall:Storage:Backend „{o.Storage.Backend}“ ungültig — 1.0 unterstützt nur „sqlite“. " +
            "Das Walhalla-Backend kehrt als NuGet-Konsument zurück, sobald Heimdall.Abstractions gepackt ist.");
    o.Storage.Backend = backend;
    if (o.Storage.RetentionDays < 0)
        throw new InvalidOperationException("Heimdall:Storage:RetentionDays darf nicht negativ sein (0 = unbegrenzt).");
    var ret = o.Storage.Retention;
    if (ret is not null && (ret.TracesDays < 0 || ret.LogsDays < 0 || ret.MetricsDays < 0))
        throw new InvalidOperationException("Heimdall:Storage:Retention:{Traces,Logs,Metrics}Days dürfen nicht negativ sein (0 = unbegrenzt, null = Fallback).");
    if (o.Storage.MaxBytes < 0)
        throw new InvalidOperationException("Heimdall:Storage:MaxBytes darf nicht negativ sein (0 = unbegrenzt).");
    if (o.Storage.RetentionSweepMinutes < 0)
        throw new InvalidOperationException("Heimdall:Storage:RetentionSweepMinutes darf nicht negativ sein (0 = deaktiviert).");
    var rollup = o.Storage.Rollup;
    if (rollup is not null)
    {
        if (rollup.ResolutionSeconds <= 0)
            throw new InvalidOperationException(
                $"Heimdall:Storage:Rollup:ResolutionSeconds „{rollup.ResolutionSeconds}“ ungültig — muss > 0 sein.");
        if (rollup.RawDays < 0)
            throw new InvalidOperationException(
                $"Heimdall:Storage:Rollup:RawDays „{rollup.RawDays}“ ungültig — negativ nicht erlaubt (0 = sofort rollen).");
        var metricsDays = o.Storage.Retention?.MetricsDays ?? o.Storage.RetentionDays;
        if (rollup.Enabled && metricsDays > 0 && rollup.RawDays > metricsDays)
            throw new InvalidOperationException(
                $"Heimdall:Storage:Rollup:RawDays „{rollup.RawDays}“ > MetricsDaysEffective „{metricsDays}“ — " +
                "Rollup-Fenster wäre leer (Raw wird vor dem Rollen gelöscht).");
    }
    // Auth-Baseline (shared Lib-Validierung: Enabled erfordert Password) +
    // Host-spezifischer ApiKey-Zwang (der Host exponiert immer OTLP/HTTP).
    o.Auth.Validate();
    if (o.Auth.Enabled && string.IsNullOrEmpty(o.Auth.ApiKey))
        throw new InvalidOperationException("Heimdall:Auth:Enabled=true erfordert ApiKey (x-heimdall-key) — der Host exponiert immer OTLP/HTTP + Prom-API.");
    if (o.Otlp.Http.MaxConcurrentRequests < 0)
        throw new InvalidOperationException("Heimdall:Otlp:Http:MaxConcurrentRequests darf nicht negativ sein (0 = unbegrenzt).");
    if (o.Otlp.Grpc.MaxConcurrentRequests < 0)
        throw new InvalidOperationException("Heimdall:Otlp:Grpc:MaxConcurrentRequests darf nicht negativ sein (0 = unbegrenzt).");
    if (o.Alerting.Enabled && o.Alerting.Smtp.Enabled &&
        (string.IsNullOrEmpty(o.Alerting.Smtp.Host) || string.IsNullOrEmpty(o.Alerting.Smtp.From) || string.IsNullOrEmpty(o.Alerting.Smtp.To)))
        throw new InvalidOperationException("Heimdall:Alerting:Smtp:Enabled=true erfordert Host, From und To.");
    if (o.Alerting.Enabled && o.Alerting.Webhook.Enabled && string.IsNullOrEmpty(o.Alerting.Webhook.Url))
        throw new InvalidOperationException("Heimdall:Alerting:Webhook:Enabled=true erfordert Url.");
}

// --- Sink-Konstruktion je Backend -------------------------------------------
static (IHeimdallSink Sink, IHeimdallQuery Query, IHeimdallMetricSource Metrics, IDisposable Disposable) BuildSink(
    HeimdallHostOptions o)
{
    if (o.Storage.Backend == "sqlite")
    {
        var s = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
        {
            DataPath = o.Storage.DataPath,
            RetentionDays = o.Storage.RetentionDays,
            Retention = new Heimdall.Storage.SQLite.HeimdallRetentionOptions
            {
                TracesDays = o.Storage.Retention?.TracesDays,
                LogsDays = o.Storage.Retention?.LogsDays,
                MetricsDays = o.Storage.Retention?.MetricsDays,
            },
            MaxBytes = o.Storage.MaxBytes,
            RetentionSweepMinutes = o.Storage.RetentionSweepMinutes,
            WalMode = o.Storage.WalMode,
            AutoVacuum = o.Storage.AutoVacuum,
            VacuumMigrateLegacy = o.Storage.VacuumMigrateLegacy,
            Rollup = new Heimdall.Storage.SQLite.HeimdallRollupOptions
            {
                Enabled = o.Storage.Rollup?.Enabled ?? false,
                ResolutionSeconds = o.Storage.Rollup?.ResolutionSeconds ?? 60,
                RawDays = o.Storage.Rollup?.RawDays ?? 1,
            },
        });
        return (s, s, s, s);
    }

    // 1.0 liefert SQLite-only. Das Walhalla-Backend kehrt zurück, sobald Walhalla
    // als NuGet-Paket vorliegt (statt der früheren cross-repo ProjectReference).
    throw new InvalidOperationException(
        "Heimdall:Storage:Backend=„" + o.Storage.Backend + "“ wird in 1.0 nicht " +
        "unterstützt. 1.0 liefert SQLite-only (Backend=„sqlite“). Das Walhalla-Backend " +
        "kehrt als NuGet-Konsument zurück, sobald Heimdall.Abstractions gepackt ist.");
}

/// <summary>Test-Hook für <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program { }