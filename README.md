# Heimdall

[![build](https://github.com/HolgerTheHuck/Heimdall/actions/workflows/build.yml/badge.svg)](https://github.com/HolgerTheHuck/Heimdall/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![nuget](https://img.shields.io/nuget/v/Heimdall.Abstractions)](https://www.nuget.org/packages/Heimdall.Abstractions)

**Heimdall** ist ein eigenständiges, OTel-kompatibles .NET-Observability-System
(Traces, Metriken, Logs) — eingebettet **und** stand-alone nutzbar. Es importiert
Grafana-Dashboards (JSON) und rendert sie selbst (in-process PromQL-Engine +
server-seitiges SVG), bietet eine professionelle Web-UI, eine
Prometheus-kompatible HTTP-API, ein konfigurierbares Alarm-Subsystem und eine
index-gestützte Attributfeldsuche. Kein Collector, keine externe Datenbank, kein
SignalR — alles läuft im .NET-Prozess.

- **Backend:** SQLite (FTS5 + Attribut-Index). Implementiert `IHeimdallSink`
  (Schreiben) und `IHeimdallQuery` (Lesen) — dasselbe Objekt geht überall rein.
  Ein Walhalla-Backend ist als künftiger NuGet-Konsument vorgesehen (siehe
  [Walhalla-Backend](#walhalla-backend)).
- **Ingest:** In-Process (OTel-SDK-Exporter, kein OTLP) **oder** OTLP/HTTP +
  OTLP/gRPC (Stand-alone-Empfänger).
- **UI:** Server-gerenderte Blazor-Static-SSR unter `/otel` (Traces, Logs,
  Metriken, Endpoints, Dashboards, Alerts). JS nur als Progressive Enhancement.
- **Prometheus:** PromQL-Engine + `/api/v1/*` — Grafana kann Heimdall direkt als
  Datenquelle nutzen.

> Status: **1.0.2** — alle Tests grün, live verifiziert (CI: Windows + Linux, .NET 8/9/10).
> 1.0-Vorbereitung: SQLite-only, keine Cross-Repo-Abhängigkeiten mehr.
> Walhalla-Historie (1.0: SQLite-only, Walhalla-Backend vorausliegend) siehe
> [DESIGN.md](DESIGN.md) und [Walhalla-Backend](#walhalla-backend).

---

## Inhalt

- [Pakete](#pakete)
- [Installation](#installation)
- [Erste Schritte](#erste-schritte)
  - [Pfad A — Eingebettet (in-process, kein OTLP)](#pfad-a--eingebettet-in-process-kein-otlp)
  - [Pfad B — Stand-alone Host + OTLP](#pfad-b--stand-alone-host--otlp)
- [Dashboards & Grafana-Import](#dashboards--grafana-import)
- [Features](#features)
- [Projektstruktur](#projektstruktur)
- [Bauen aus dem Source](#bauen-aus-dem-source)

---

## Pakete

Alle Pakete sind **additiv** und greifen nicht in bestehende `Add*`/`Map*`-
Signaturen ein. Version `1.0.2`, Target-Frameworks `net8.0;net9.0;net10.0`.

| Paket | Zweck |
|---|---|
| `Heimdall.Abstractions` | Vertrag: `IHeimdallSink`, `IHeimdallQuery`, Model-Records (`HLogRecord`, `HSpan`, …), `LogSearch`/`AttrFilter`. Abhängigkeitsfrei. Das einzige Paket, das Consumer (auch Walhalla) referenzieren müssen. |
| `Heimdall.Sdk` | In-Process-Exporter für das OpenTelemetry-SDK: `AddOpenTelemetry().UseHeimdallExporter(...)`. Traces/Logs/Metriken landen direkt im Sink — ohne OTLP/HTTP/gRPC. |
| `Heimdall.Direct` | Native API (`HeimdallHub`: Tracer/Logger/Meter) für Einbettung **ohne** OTel-SDK. |
| `Heimdall.Ingest` | Bounded Buffer, Batching, Backpressure, Rekursionsschutz über `IHeimdallSink`. |
| `Heimdall.Storage.SQLite` | SQLite-Backend (Microsoft.Data.Sqlite, FTS5-Volltext, `heim_log_attrs`-Attribut-Index, Retention-Sweeper). **Empfohlen für den Einstieg.** In 1.0 das einzige Backend. |
| `Heimdall.Otlp` | OTLP/HTTP-Empfänger (`/v1/{traces,metrics,logs}`, Protobuf + JSON). |
| `Heimdall.Otlp.Proto` | OTLP-Proto-Typen + OTLP→Heimdall-Konverter (transport-agnostisch). |
| `Heimdall.Otlp.Grpc` | OTLP/gRPC-Empfänger (`localhost:4317`). Zieht `Grpc.AspNetCore`. |
| `Heimdall.Prometheus` | PromQL-Engine + Prometheus-HTTP-API (`/api/v1/*`) + RED-Ableitung aus Spans. Storage-agnostisch. |
| `Heimdall.Blazor` | Die Web-UI: Traces/Logs/Metriken/Endpoints/Dashboards/Alerts, server-gerendert. **Enthält das Alarm-Subsystem.** |
| `Heimdall.AspNetCore.Enrichment` | Dünne Middleware: taggt den OTel-Server-Span mit `aspnetmvc.controller/action/route` für den Controller/Endpoint-Drilldown. _(Paket-ID; Namespace bleibt `Heimdall.AspNetCore`.)_ |

---

## Installation

### 1. Pakete beziehen

Ab der 1.0 liegen die Heimdall-Pakete auf **nuget.org** — dann reicht ein schlichtes
`dotnet add package Heimdall.Sdk` (bzw. `--version 1.0.2`). Für Pre-Release- oder
Integrationsbuilds liegt ein lokaler Feed unter `artifacts/nupkg/`; diesen als
NuGet-Source hinzufügen (z. B. in der `nuget.config` eures API-Projekts):

```bash
# im Projektverzeichnis der realen API; <HEIMDALL-REPO> = Pfad zum Heimdall-Checkout
dotnet nuget add source "<HEIMDALL-REPO>/artifacts/nupkg" -n HeimdallLocal
```

oder als `nuget.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="HeimdallLocal" value="<HEIMDALL-REPO>/artifacts/nupkg" />
  </packageSources>
</configuration>
```

### 2. Pakete neu bauen (optional)

Falls sich der Code geändert hat, Pakete neu erzeugen:

```bash
cd Heimdall
# packt alle Heimdall-Src-Projekte (IsPackable=true) nach artifacts/nupkg
# (Version 1.0.2 zentral in Directory.Build.props):
dotnet pack Heimdall.slnx -c Release -o artifacts/nupkg
```

### 3. Pakete referenzieren

Für die **in-Process-Einbettung** (Pfad A) in eurer realen API:

```bash
dotnet add package Heimdall.Sdk          --version 1.0.2
dotnet add package Heimdall.Storage.SQLite --version 1.0.2
dotnet add package Heimdall.Blazor         --version 1.0.2
dotnet add package Heimdall.Prometheus     --version 1.0.2
dotnet add package Heimdall.AspNetCore.Enrichment --version 1.0.2
```

Heimdall-Pakete ziehen einander (jedes hängt an `Heimdall.Abstractions`); die
öffentlichen OpenTelemetry-Abhängigkeiten kommen von nuget.org.

---

## Erste Schritte

Heimdall kennt zwei Einbettungspfade. **Pfad A** ist der schnellste für „eine reale
API instrumentieren und das Dashboard sehen" — alles im Prozess, kein Collector.

### Pfad A — Eingebettet (in-process, kein OTLP)

Eine ASP.NET-Core-WebAPI, die ihre Telemetrie per Heimdall-SDK-Exporter direkt in
den eingebetteten SQLite-Sink schreibt und das Dashboard im selben Prozess
bereitstellt.

`Program.cs`:

```csharp
using Heimdall;
using Heimdall.AspNetCore;
using Heimdall.Blazor;
using Heimdall.Prometheus;
using Heimdall.Sdk;
using Heimdall.Storage.SQLite;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// 1) Backend: SQLite ist IHeimdallSink (Schreiben) UND IHeimdallQuery (Lesen).
var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
{
    DataPath = "heimdall.db",     // persistent; RetentionDays=7 default
});

// 2) Dashboard + Prometheus + Grafana-Store registrieren (das selbe Sink-Objekt).
builder.Services.AddHeimdallDashboard(sink)
    .AddHeimdallPrometheus(sink, sink)          // PromQL + RED aus Spans
    .AddHeimdallDashboards("./dashboards");      // dateibasierter Grafana-Store

// 3) OTel-Resource: service.name/version + deployment.environment + host.name
//    (werden vom SQLite-Backend als Metrik-Label übernommen → service_name-Filter
//    des otel-dotnet-webapi-Dashboards greifen).
var exporterOpts = new HeimdallExporterOptions
{
    Sink = sink,
    ServiceName = "MyApi",
    ServiceVersion = "1.0.0",
    ResourceAttributes = new[]
    {
        new HAttribute("deployment.environment", "production"),
        new HAttribute("host.name", Environment.MachineName),
    },
    MetricExportIntervalMs = 15_000,           // 15 s → rate()-Fenster füllen sich schnell
};

builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage = true; o.IncludeScopes = true; });

// 4) OTel-SDK: alle 3 Signale direkt in den Heimdall-Sink (in-process).
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation()
                       .AddHttpClientInstrumentation()
                       .UseHeimdallExporter(exporterOpts))
    .WithMetrics(m => m.AddAspNetCoreInstrumentation()
                       .AddHttpClientInstrumentation()
                       .AddRuntimeInstrumentation()
                       .UseHeimdallExporter(exporterOpts))
    .WithLogging(l => l.UseHeimdallExporter(exporterOpts));

// 5) Controller/Endpoint-Drilldown: taggt Server-Spans mit echten Namen.
builder.Services.AddHeimdallAspNetCore();

builder.Services.AddControllers();

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseHeimdallAspNetCore();        // nach UseRouting, vor MapControllers
app.MapControllers();
app.MapHeimdallDashboard("/otel"); // UI + Dashboards unter /otel
app.MapHeimdallPrometheus("/otel"); // Prom-HTTP-API (/api/v1/*) — Grafana-kompatibel
app.Run();
```

Starten:

```bash
dotnet run
```

Dann:

- **Dashboard:** `http://localhost:<port>/otel` (Übersicht, Traces, Logs, Metriken,
  Endpoints, Alerts)
- **Prometheus-API:** `http://localhost:<port>/otel/api/v1/query?query=...`
  (Grafana kann hier als Datenquelle zeigen)
- **Logs-Feldsuche:** `http://localhost:<port>/otel/logs?q={service.name="MyApi"} |= "error"`

Die reale API erzeugt nun echte Server-Spans, ASP.NET-/Runtime-Metriken und Logs,
die sofort im Dashboard erscheinen.

### Eingebettet hinter einem Login (opt-in)

Heimdall lässt sich **opt-in** hinter einen Name/Passwort-Login verbauen —
nützlich, wenn das eingebettete Dashboard in einer App erreichbar ist, die nicht
jeden Besucher zur Observability-Oberfläche lassen soll. Die Auth lebt in der
Bibliothek (`Heimdall.AspNetCore`, `UseHeimdallAuth`) und ist dieselbe, die der
Stand-alone-Host nutzt; konfiguriert wird sie in `appsettings.json`:

```json
"Heimdall": { "Auth": { "Enabled": true, "Username": "admin", "Password": "change-me", "ApiKey": "change-me-too" } }
```

Im Host der App drei Zeilen (vor `UseStaticFiles`/`Map*`):

```csharp
var auth = builder.Configuration.GetSection("Heimdall:Auth").Get<HeimdallAuthOptions>() ?? new();
auth.ProtectedPrefix = "/otel";   // nur /otel/* schützen — App-Routes (/api/…) bleiben frei
auth.Validate();
…
app.UseHeimdallAuth(auth);
```

- **UI** (`/otel/*`): HTTP-Basic-Auth (browser-nativer Login-Dialog, kein JS) gegen
  `Username` + `Password`. `Username` null = beliebiger Name (Shared-Password).
- **Prom-API** (`/otel/api/v1/*`): Header `x-heimdall-key` gegen `ApiKey`
  (Header only — kein Query-Fallback; Query-Strings landen in Access-Logs).
- **`ProtectedPrefix`**: nur dieser Subtree wird geschützt; die **App-eigenen
  Routes bleiben frei** (im Gegensatz zum Stand-alone-Host, dessen Routes
  sämtlich Heimdalls sind → dort kein Prefix, globale Auth).
- **`Enabled=false`** (Default) = Zero-Overhead-Passthrough — bestehende
  Deployments unverändert.
- Vergleiche **zeitkonstant** (`SecretComparer`/`FixedTimeEquals`).

### Pfad B — Stand-alone Host + OTLP

Wenn die reale API bereits OTLP exportiert (oder soll), läuft Heimdall als
eigenständiger Empfänger (Grafana-Stack-Äquivalent), konfiggetrieben:

```bash
cd Heimdall
dotnet run --project host/Heimdall.Host -c Release
# UI:        http://localhost:5099/otel
# OTLP/HTTP: POST http://localhost:5099/otel/v1/{traces,metrics,logs}
# OTLP/gRPC: localhost:4317
# Prom-API:  http://localhost:5099/otel/api/v1/*
```

Die reale API bekommt das normale OTel-SDK mit OTLP-Exporter, der auf den Host
zeigt:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation()
                       .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithMetrics(m => m.AddRuntimeInstrumentation()
                       .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithLogging(l => l.AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")));
```

Die Host-Konfiguration liegt in `host/Heimdall.Host/appsettings.json` (Sektion
`Heimdall`): Backend (1.0: `sqlite`), DataPath, Retention, OTLP-HTTP/gRPC (je
`MaxConcurrentRequests`-Cap, C1), Prometheus, Dashboard, Alerting, Auth
(`ApiKey`/`Username`/`Password`), SeedDemoData. Siehe auch `host/Heimdall.Host/README.md`.

---

## Dashboards & Grafana-Import

Heimdall importiert Grafana-Dashboard-JSON und rendert sie selbst — unabhängig von
einem laufenden Grafana. Unterstützte Panel-Typen: Timeseries, Stat
(`graphMode=area` → Kachel mit Sparkline), Table, Gauge, BarGauge, Pie, Heatmap,
Logs (Loki via eigenem Log-Store). Template-Variablen (`$service_name`,
`$http_route`, …) werden als `var-*`-Query-Parameter übergeben.

- Import: `http://localhost:<port>/otel/dashboards/import` (Datei-Upload oder JSON).
- Datei-Store: Verzeichnis aus `AddHeimdallDashboards(dir)` bzw. Host
  `Heimdall:DashboardsStore:Dir`.
- Beispiel-Dashboard: `otel-dotnet-webapi` (gnetId 20568, .NET-OTel-WebAPI) liegt
  in `host/Heimdall.Host/var/heimdall/dashboards/`.

Alternativ nutzt ihr ein echtes Grafana mit Heimdall als Prometheus-Datenquelle
(`http://host/otel/api/v1`).

---

## Features

- **Traces:** Listen, Wasserfall-Detail, Filter (Name/Service/Fehler), Zeitraum.
  Controller/Endpoint-Drilldown (`/otel/endpoints`) aus `aspnetmvc.*`-Tags.
- **Metriken:** Metrik-Serien, Prometheus-PromQL (`rate`, `histogram_quantile`,
  `topk`, …), RED-Ableitung (Rate/Errors/Duration) aus Server-Spans, Runtime-Metriken.
- **Logs:** Seq-style Tabelle (volle Breite, ganze Zeile aufklappbar),
  Body-Volltext (FTS5) **und** index-gestützte Attributfeldsuche
  (`{service.name="x"} |= "text"`, Op `= != =~ !~`, Key-Norm `.`↔`_`), strict
  Loki-Semantik. Deckt Log- **und** Resource-Attribute (`service.name`).
- **Alerting:** Regeln über Logs/Metriken/Traces, Zustandsautomat
  (OK→Pending→Firing→Resolved + Dedup), Kanäle Logger/SMTP/Webhook, file-basierte
  Rule- und State-Stores, UI unter `/otel/alerts`. Lebt vollständig in
  `Heimdall.Blazor`.
- **Prometheus-API:** `/api/v1/{query,query_range,labels,series,status/buildinfo}`
  + Text-Exposition. Range-Query-Prefetch (Dashboard-Render 108 s → 0,8 s).
- **UI:** Responsive, Dark-Only, Crosshair/Brushing, moderner Zeitbereich +
  Auto-Refresh, server-gerendert (kein SignalR).
- **Auth (Host):** minimal — API-Key (`x-heimdall-key`, Header only) für OTLP +
  Prom-API, Basic-Auth für die UI, via Prefix-Middleware. **Zeitkonstanter**
  Vergleich (`SecretComparer`/`FixedTimeEquals`); kein Query-Fallback (Access-Log-
  Hygiene). Bibliotheken bleiben auth-frei.
- **Admission Control (Host, C1):** Concurrency-Cap auf den OTLP-Empfängern
  (`Heimdall:Otlp:{Http,Grpc}:MaxConcurrentRequests`, Default 32, `0`=unbegrenzt);
  Überlauf → HTTP 429 / gRPC `ResourceExhausted` (retrybar). Schützt den
  Single-Connection-SQLite-Sink vor Last-Spitzen.
- **Host-Self-Observability (C3):** synthetisierte `heimdall.host.*`-Metriken —
  Ingest-Volume pro Signal (`heimdall.host.ingest{signal=…}`, Prom `*_total`) +
  Sweep-Latenz (`heimdall.host.sweep.duration`, Prom `*_seconds`); in-memory, nicht
  in `heim_metrics` gespeichert (kein Selbst-Feedback). Ergänzt A4
  (`heimdall.retention.*`/`heimdall.storage.*`).
- **Graceful Shutdown (C4):** Sink-Dispose nach Kestrel-Drain
  (`ApplicationStopped`); in-flight OTLP-Writes committen vor dem Verbindungs-
  Abbau; `IngestBuffer` draint beim Stopp vollständig.

---

## Projektstruktur

```
src/
  Heimdall.Abstractions/     Vertrag (Schnittstellen + Records)
  Heimdall.Sdk/               OTel-SDK in-process Exporter
  Heimdall.Direct/            Native API ohne OTel-SDK
  Heimdall.Ingest/            Buffer/Batching/Backpressure
  Heimdall.Storage.SQLite/    SQLite-Backend (empfohlen, 1.0 einziges Backend)
  Heimdall.Otlp(.Proto/.Grpc)/ OTLP-Empfänger (HTTP + gRPC)
  Heimdall.Prometheus/        PromQL + Prom-HTTP-API
  Heimdall.Blazor/            Web-UI + Alerts + Grafana-Renderer
  Heimdall.AspNetCore/        Controller/Endpoint-Enrichment
host/Heimdall.Host/           Stand-alone Host (config-getrieben, Docker)
samples/
  Heimdall.OtelSample/        WebAPI + in-process Exporter + Live-Dashboard
  Heimdall.MvcSample/         WebAPI + Controller/Endpoint-Drilldown
tests/Heimdall.Tests/         xUnit, net8/9/10 (siehe CI-Badge)
artifacts/nupkg/              lokale NuGet-Pakete (1.0.2)
```

---

## Bauen aus dem Source

```bash
cd Heimdall

# Solution bauen (net8/9/10)
dotnet build Heimdall.slnx -c Release

# Tests
dotnet test tests/Heimdall.Tests/Heimdall.Tests.csproj -c Release --no-build

# Samples starten (jeweils eigenständig, frische Temp-DB)
dotnet run --project samples/Heimdall.OtelSample   # http://localhost:5198/otel
dotnet run --project samples/Heimdall.MvcSample    # http://localhost:5199/otel
```

Voraussetzungen: .NET 8/9/10 SDK. 1.0 ist SQLite-only und hat keine
Cross-Repo-Abhängigkeiten — ein kompletter Klon baut und testet ohne das
Nachbarrepo `../Walhalla`.

## Walhalla-Backend

1.0 liefert nur das SQLite-Backend. Das frühere `Heimdall.Storage.Walhalla`
(konsumierte die eingebettete WalhallaSql-Engine via cross-repo
`ProjectReference`) ist aus Heimdall entfernt. Die Vertrags-Schicht
`Heimdall.Abstractions` ist so angelegt, dass Walhalla künftig als
**NuGet-Konsument** wiederkommt: sobald `Heimdall.Abstractions` gepackt ist,
referenziert Walhalla nur noch dieses Paket (keine Zirkelabhängigkeit mehr),
und ein separates `Heimdall.Storage.Walhalla`-Paket kann das Backend als
NuGet-Abhängigkeit wieder anbieten. Bis dahin gilt `Backend=sqlite`.