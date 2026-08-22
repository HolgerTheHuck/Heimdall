using Heimdall;
using Heimdall.AspNetCore;
using Heimdall.Blazor;
using Heimdall.Blazor.Alerts;
using Heimdall.OtelSample.Store;
using Heimdall.OtelSample.Traffic;
using Heimdall.Prometheus;
using Heimdall.Sdk;
using Heimdall.Storage.SQLite;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// --- Heimdall.OtelSample -----------------------------------------------------
// ASP.NET-Core-WebAPI mit OpenTelemetry (traces + metrics + logs), die ihre
// Telemetrie per in-process Heimdall-Exporter DIREKT in den eingebetteten
// Heimdall-Sink schreibt — ohne OTLP, ohne Collector. Heimdall ist zugleich das
// Dashboard: PromQL-Engine + Grafana-Renderer laufen im selben Prozess und
// werten das importierte otel-dotnet-webapi-Dashboard (gnetId 20568) gegen die
// so erzeugten echten Metriken aus.
//
// Start:     dotnet run --project samples/Heimdall.OtelSample
// API:       http://localhost:5198/api/{products,orders}
// Dashboard: http://localhost:5198/otel/dashboards/otel-dotnet-webapi
// Übersicht: http://localhost:5198/otel
//
// Heimdall als „Swagger-UI für WebAPI": ein `AddHeimdallDashboard` + drei
// `UseHeimdallExporter`, nicht aufdringlich, alles embedded.

const string Url = "http://localhost:5198";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Url);

// Optionaler Login-Schutz für die Heimdall-Oberfläche (opt-in via appsettings
// Sektion "Heimdall:Auth": Enabled=true + Username/Password + ggf. ApiKey).
// ProtectedPrefix="/otel" → nur /otel/* wird geschützt; die App-eigenen Routes
// (/api/{products,orders}, /) bleiben frei. Enabled=false (Default) =
// Zero-Overhead-Passthrough (Status quo). Validate() wirft fail-fast bei
// Enabled ohne Password.
var auth = builder.Configuration.GetSection("Heimdall:Auth")
                    .Get<HeimdallAuthOptions>() ?? new HeimdallAuthOptions();
auth.ProtectedPrefix = "/otel";
auth.Validate();

// Eingebettetes Backend: SQLite implementiert IHeimdallSink (OTel-Exporter-Ziel)
// UND IHeimdallQuery (Dashboard-Lesevertrag). Frische DB pro Start, damit das
// Dashboard mit sauberen Daten befüllt wird.
var sink = BuildSink();
builder.Services.AddHeimdallDashboard(sink)
    .AddHeimdallPrometheus(sink, sink)   // PromQL-Engine + RED-Ableitung aus Spans
    .AddHeimdallDashboards(DashboardsDir()) // dateibasierter Grafana-Dashboard-Store
    .AddHeimdallAlerting(sink, new HeimdallAlertingOptions   // Alarm-Subsystem (Logger-Kanal, Demo)
    {
        Enabled = true,
        LoggerEnabled = true,
        EvaluationIntervalSeconds = 10,
        RulesDir = Path.Combine(AlertsDir(), "rules"),
        StateDir = AlertsDir(),
    });

// OTel-Resource: service.name/version + deployment.environment + host.name. Diese
// Resource-Attribute werden vom SQLite-Backend als Label JE Metrikpunkt übernommen
// (resource_to_telemetry_conversion) und — via service.name→job+service_name —
// vom PromQL-Layer exponiert. So greifen die service_name/deployment_environment/
// host_name-Filter des otel-dotnet-webapi-Dashboards.
var resource = new HeimdallExporterOptions
{
    Sink = sink,
    ServiceName = "OtelSample",
    ServiceVersion = "1.0.0",
    ResourceAttributes = new[]
    {
        new HAttribute("deployment.environment", "production"),
        new HAttribute("host.name", Environment.MachineName),
    },
    // 15-s-Export-Kadenz (statt SDK-Default 60 s): so sehen rate()-Fenster (~1 m)
    // mehrere Punkte und die Histogramm-Dauer-Panels (p95/p50) füllen sich sofort
    // mit echten Werten statt leer zu bleiben.
    MetricExportIntervalMs = 15_000,
};

// ILogger → OTel-Logs: formatierten Message-Body übernehmen + Scopes anreichern,
// damit die Loki-Logs-Panels des Dashboards aussagekräftige Zeilen sehen.
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});

// OTel-SDK: alle 3 Signale direkt in den Heimdall-Sink (in-process, kein OTLP).
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .UseHeimdallExporter(resource))
    .WithMetrics(m => m
        // ASP.NET-Core-Nativmetriken (ab .NET 8 über System.Diagnostics.Metrics):
        //   Microsoft.AspNetCore.Hosting  -> http.server.request.duration [s, Hist],
        //                                  http.server.active_requests [Gauge]
        //   Microsoft.AspNetCore.Routing  -> aspnetcore.routing.match_attempts [Counter]
        //   Microsoft.AspNetCore.Server.Kestrel -> kestrel.active_connections [Gauge]
        //   System.Net.Http              -> http.client.request.duration [Hist],
        //                                  http.client.active_requests/open_connections
        // Genach das, was das otel-dotnet-webapi-Dashboard auswertet (RED + USE).
        .AddMeter("Microsoft.AspNetCore.Hosting")
        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
        .AddMeter("Microsoft.AspNetCore.Routing")
        .AddMeter("System.Net.Http")             // HttpClient-RED (TrafficService → self)
        // Runtime/Process-Metriken (GC, ThreadPool, Memory, Exceptions, CPU, …):
        // füllt die process.*-/process_runtime_dotnet_*-Panels des Dashboards.
        .AddRuntimeInstrumentation()
        .UseHeimdallExporter(resource))
    .WithLogging(l => l.UseHeimdallExporter(resource));

// Heimdall-Span-Enrichment: taggt Activity.Current mit aspnetmvc.controller/
// action/route → Controller/Endpoint-Drilldown unter /otel/endpoints.
builder.Services.AddHeimdallAspNetCore();

// In-Memory-Datenbestand + Hintergrund-Traffic (erzeugt echte Server-Spans,
// Metriken und Logs gegen die eigene WebAPI).
builder.Services.AddSingleton<DataStore>();
builder.Services.AddHttpClient("self", c => c.BaseAddress = new Uri(Url));
builder.Services.AddHostedService<TrafficService>();

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

// Opt-in Login-Schutz (vor UseStaticFiles + Map*; Passthrough bei Enabled=false).
app.UseHeimdallAuth(auth);

app.UseStaticFiles();                 // heimdall.css/js aus _content
app.UseRouting();
app.UseHeimdallAspNetCore();          // nach UseRouting, vor MapControllers
app.MapControllers();
app.MapHeimdallDashboard("/otel");    // UI + Dashboards unter /otel
app.MapHeimdallPrometheus("/otel");   // Prom-HTTP-API (/api/v1/*) — Grafana-kompatibel
app.MapGet("/", () => Results.Redirect("/otel/dashboards/otel-dotnet-webapi", permanent: false));

// Demo-Alarmregeln seeden (5xx-Rate + Fehler-Log-Haeufung, Logger-Kanal) — idempotent.
try { AlertDemoRules.Seed(app.Services.GetRequiredService<IAlertRuleStore>()); }
catch { /* Seeding optional */ }

app.Run();

// Baut das SQLite-Backend (frische DB pro Start) — IHeimdallSink + IHeimdallQuery.
static SQLiteTelemetrySink BuildSink()
{
    var path = Path.Combine(Path.GetTempPath(), "heimdall-otelsample.db");
    if (File.Exists(path)) File.Delete(path);
    return new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });
}

// Verzeichnis mit dem importierten Dashboard (wird per CopyToOutputDirectory aus
// dashboards/ ins Ausgabeverzeichnis gelegt und beim ersten Zugriff erzeugt).
static string DashboardsDir()
    => Path.Combine(AppContext.BaseDirectory, "dashboards");

// Frisches Alert-Verzeichnis pro Start (Regeln + Zustand) — wie die Demo-DB.
static string AlertsDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "heimdall-otelsample-alerts");
    if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
    return dir;
}