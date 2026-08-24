using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// --- Heimdall.OtlpSender -----------------------------------------------------
// End-to-End-CLIENT für den Stand-alone-Host: erzeugt mit dem echten OTel-SDK
// feste Signale (Traces + Metriken + Logs) und exportiert sie via OTLP/HTTP
// (Protobuf) oder OTLP/gRPC an Heimdall. Heimdall ist der Empfänger; dieser
// Sender validiert die echte Wire des Hosts (Endpoint-Konvention, Header-Auth,
// Protokoll-Switch, Retries) — nicht Roh-Protobuf per curl.
//
// Aufruf:
//   dotnet run --project samples/Heimdall.OtlpSender -- \
//     --protocol http --endpoint http://localhost:5099/otel
//   dotnet run --project samples/Heimdall.OtlpSender -- \
//     --protocol grpc --endpoint http://localhost:4317 \
//     --header "x-heimdall-key=change-me"
//
// Endpoint-Konvention: HTTP = Basis-URL (z. B. http://localhost:5099/otel); das
// SDK nutzt sie VERBATIM, daher baut der Sender pro Signal den vollen Pfad
// {base}/v1/{traces,metrics,logs}. gRPC = Host:Port (proto-fixierte Service-Pfade).
// Alternativ Env: OTEL_EXPORTER_OTLP_PROTOCOL (http/protobuf|grpc),
//   OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_HEADERS="x-heimdall-key=…".

var protocol = ParseArg(args, "protocol", env: "OTEL_EXPORTER_OTLP_PROTOCOL", def: "http");
var useGrpc = protocol.Equals("grpc", StringComparison.OrdinalIgnoreCase);
var endpoint = ParseArg(args, "endpoint", env: "OTEL_EXPORTER_OTLP_ENDPOINT",
    def: useGrpc ? "http://localhost:4317" : "http://localhost:5099/otel");
var header = ParseArg(args, "header", env: "OTEL_EXPORTER_OTLP_HEADERS", def: "");

var otlpProtocol = useGrpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
var endpointUri = new Uri(endpoint);

// Eindeutiger Marker pro Protokoll-Lauf, damit HTTP- und gRPC-Daten im Storage
// unterscheidbar bleiben (Service-Name + Log-Body + Span-Attribut).
var runTag = useGrpc ? "grpc" : "http";
var serviceName = $"oltp-sender-e2e-{runTag}";

// Endpoint-Konvention (empirisch für OpenTelemetry.Exporter 1.16.0 geklärt):
// Das SDK nutzt OtlpExporterOptions.Endpoint VERBATIM als POST-Ziel — es appendet
// NICHT /v1/{signal} (ein自定义 Endpoint geht 1:1 über die Wire). Daher pro Signal
// der volle Pfad: HTTP {base}/v1/{traces,metrics,logs}. gRPC nutzt den Endpoint
// als Channel-Target (proto-fixierte Service-Pfade, kein Pfad-Anteil).
void ConfigureOtlp(OtlpExporterOptions o, string signal)
{
    o.Protocol = otlpProtocol;
    if (!string.IsNullOrEmpty(header)) o.Headers = header;
    o.Endpoint = useGrpc
        ? endpointUri
        : new Uri(endpoint.TrimEnd('/') + "/v1/" + signal);
}

Console.WriteLine($"Heimdall.OtlpSender: protocol={otlpProtocol}, endpoint={endpointUri}, " +
                 $"header={(string.IsNullOrEmpty(header) ? "(none)" : header)}, service={serviceName}");

var resource = ResourceBuilder.CreateDefault()
    .AddService(serviceName, serviceVersion: "1.0");

// --- Traces: 1 Trace, 2 Spans (server + client) -----------------------------
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource("Heimdall.OtlpSender")
    .AddOtlpExporter(o => ConfigureOtlp(o, "traces"))
    .Build();

using var activitySource = new ActivitySource("Heimdall.OtlpSender");
using (var root = activitySource.StartActivity("e2e-root", ActivityKind.Server))
{
    root?.SetTag("e2e.marker", serviceName);
    using (var child = activitySource.StartActivity("e2e-child", ActivityKind.Client))
    {
        child?.SetTag("e2e.marker", serviceName);
    }
}

// --- Metriken: Counter + Histogram ------------------------------------------
using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter("Heimdall.OtlpSender")
    .AddOtlpExporter(o => ConfigureOtlp(o, "metrics"))
    .Build();

var meter = new Meter("Heimdall.OtlpSender");
var counter = meter.CreateCounter<long>("heimdall_e2e_oltp_test_total");
counter.Add(5, new KeyValuePair<string, object?>("protocol", runTag));
var hist = meter.CreateHistogram<double>("heimdall_e2e_oltp_test_duration", unit: "ms");
hist.Record(12.5);
hist.Record(42.0);
hist.Record(7.3);
meterProvider.ForceFlush();

// --- Logs: 3 Einträge (Info/Warn/Error) -------------------------------------
using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddFilter((_, _) => true)
    .AddOpenTelemetry(o =>
    {
        o.SetResourceBuilder(resource);
        o.IncludeFormattedMessage = true;
        o.AddOtlpExporter(oo => ConfigureOtlp(oo, "logs"));
    }));
var log = loggerFactory.CreateLogger("Heimdall.OtlpSender");
log.LogInformation("e2e-oltp-marker/{Run}: info log via {Protocol}", runTag, otlpProtocol);
log.LogWarning("e2e-oltp-marker/{Run}: warn log via {Protocol}", runTag, otlpProtocol);
log.LogError("e2e-oltp-marker/{Run}: error log via {Protocol}", runTag, otlpProtocol);

// Force-Flush + geordneter Shutdown (Export garantiert vor Programmende).
tracerProvider.ForceFlush();
meterProvider.ForceFlush();
Console.WriteLine($"Heimdall.OtlpSender: done — 1 trace/2 spans, 1 counter(+5), 1 histogram(3), 3 logs ({runTag}).");

// --- Arg-Parser -------------------------------------------------------------
static string ParseArg(string[] args, string name, string env, string def)
{
    var flag = "--" + name;
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    var envVal = Environment.GetEnvironmentVariable(env);
    return string.IsNullOrEmpty(envVal) ? def : envVal;
}