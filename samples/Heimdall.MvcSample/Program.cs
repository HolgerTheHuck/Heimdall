using Heimdall;
using Heimdall.AspNetCore;
using Heimdall.Blazor;
using Heimdall.Blazor.Alerts;
using Heimdall.MvcSample.Store;
using Heimdall.MvcSample.Traffic;
using Heimdall.Sdk;
using Heimdall.Storage.SQLite;
using OpenTelemetry;
using OpenTelemetry.Trace;

// --- Heimdall.MvcSample: echte ASP.NET-Core-WebAPI (4 Controller, CRUD + Order)
// mit eingebettetem Heimdall. Zeigt End-to-End wie die Heimdall.AspNetCore-Middleware
// die Server-Spans mit echten controller/action-Tags anreichert und der 2-stufige
// Drilldown API -> Controller -> Endpoint unter /otel/endpoints mit echtem Traffic
// sichtbar wird.
// Start: `dotnet run --project samples/Heimdall.MvcSample` -> http://localhost:5199/otel
// API:        http://localhost:5199/api/{kunden,adressen,artikel,bestellungen}
// Dashboard:  /otel            (Gesamt)
//            /otel/endpoints  (API -> Controller -> Endpoint, aus Server-Spans)

var builder = WebApplication.CreateBuilder(args);

// Eingebettetes Backend: SQLite implementiert IHeimdallSink (OTel-Exporter) UND
// IHeimdallQuery (Dashboard). Frische DB pro Start.
var sink = BuildSink();
builder.Services.AddHeimdallDashboard(sink)
    .AddHeimdallAlerting(sink, new HeimdallAlertingOptions   // Alarm-Subsystem (Logger-Kanal, Demo)
    {
        Enabled = true,
        LoggerEnabled = true,
        EvaluationIntervalSeconds = 10,
        RulesDir = Path.Combine(AlertsDir(), "rules"),
        StateDir = AlertsDir(),
    });

// In-Memory-Store + Live-Traffic-Seeder.
builder.Services.AddSingleton<DataStore>();
builder.Services.AddHttpClient("mvc", c => c.BaseAddress = new Uri("http://localhost:5199"));
builder.Services.AddHostedService<TrafficService>();

// OTel-SDK: AspNetCore- + Http-Instrumentation -> Heimdall in-process Exporter
// (kein OTLP, kein Collector). Traces landen direkt im SQLite-Sink.
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .UseHeimdallExporter(sink, "MvcSample"));

// Heimdall-Enrichment: taggt Activity.Current mit aspnetmvc.controller/action/route.
builder.Services.AddHeimdallAspNetCore();

// MVC-Controller + Swagger-freundliche JSON-Optionen.
builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.UseStaticFiles();                                   // heimdall.css aus _content
app.UseRouting();
app.UseHeimdallAspNetCore();                             // nach UseRouting, vor MapControllers
app.MapControllers();
app.MapHeimdallDashboard("/otel");
app.MapGet("/", () => Results.Redirect("/otel", permanent: false));

// Demo-Alarmregeln seeden (Logger-Kanal) — idempotent.
try { AlertDemoRules.Seed(app.Services.GetRequiredService<IAlertRuleStore>()); }
catch { /* Seeding optional */ }

app.Run();

// Baut das SQLite-Backend (frische DB) — implementiert IHeimdallSink + IHeimdallQuery.
static SQLiteTelemetrySink BuildSink()
{
    var path = Path.Combine(Path.GetTempPath(), "heimdall-mvcsample.db");
    if (File.Exists(path)) File.Delete(path);
    return new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });
}

// Frisches Alert-Verzeichnis pro Start (Regeln + Zustand) — wie die Demo-DB.
static string AlertsDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "heimdall-mvcsample-alerts");
    if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
    return dir;
}