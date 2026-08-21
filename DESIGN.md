# Heimdall — eingebaute OTel-Infrastruktur für .NET

> Heimdall ist der Wächter der Götter, der alles sieht und hört — passend zu einem
> Observability-Tool im nordischen Themenkreis neben Walhalla.
> (Name nur ein Vorschlag; das Repo heißt aktuell `Telnet`.)

> **Status (Wege zur 1.0):** 1.0 liefert **SQLite-only** und trägt keine
> Cross-Repo-Abhängigkeiten mehr. Das `Heimdall.Storage.Walhalla`-Backend ist
> aus Heimdall entfernt; die nachfolgend beschriebene Walhalla-Speicherebene
> und die Walhalla-Selbst-Observability (§3.9) sind daher in 1.0 **vorausliegend**
> und kehren zurück, sobald `Heimdall.Abstractions` als NuGet-Paket vorliegt und
> Walhalla ausschließlich dieses referenziert (keine Zirkelabhängigkeit mehr).
> Die Vertrags-Schicht (`Heimdall.Abstractions`) und die SQLite-Implementierung
> entsprechen weiterhin diesem Dokument.

## 1. Problem

Zwei eigene .NET-Projekte (API-Plattform, SQL-DB) nutzen OpenTelemetry, aber es gibt
für .NET keine **kleine, eingebettbare** OTel-Infrastruktur, die gleichzeitig

- das OTLP-Protokoll **empfängt**,
- Traces / Logs / Metriken **speichert** und
- sie **anzeigt**.

Standard-Stacks (Jaeger/Tempo + Loki + Prometheus + Grafana) sind kein Embedding
geeignet — mehrere externe Prozesse, hohe Betriebslast. Ziel ist **ein Assembly**,
das man in eine App einbindet (auch aus F#) und das ohne externe Prozesse läuft.

## 2. Leitentscheidungen

| Entscheidung | Wahl | Begründung |
|---|---|---|
| OTLP-Transport | **OTLP/HTTP + Protobuf** (plus JSON) **und OTLP/gRPC** | HTTP ist der moderne Default des .NET-OTel-Exporters; gRPC deckt Apps ab, die per gRPC exportieren. gRPC in eigenem Package `Heimdall.Otlp.Grpc` |
| Protokoll-Abhängigkeit | nur `OpenTelemetry.Proto` (generierte Messages), **nicht** den OTel-SDK/Collector | kleine Oberfläche, embeddable |
| Storage | **WalhallaSql embedded** via `WalhallaEngine` | keine externen DB-Prozesse, MVCC/WAL, eingebaute **Fulltext-Suche** für Traces/Logs |
| Ingestion embedded | **direkt, in-process** — OTel-SDK-Exporter `Heimdall.Sdk` ODER native API `Heimdall.Direct`, ohne Netzwerk | kein OTLP-Exporter-Umweg; OTLP-Empfänger nur für fremde Prozesse |
| Selbst-Observability | **Walhalla instrumentiert sich selbst** über Heimdall (welche Tabelle wie oft, Logs, Plan-Cache, WAL/MVCC) | echte Stand-Alone-Variante der DB; Heimdall ist die eingebaute Telemetrie der DB |
| Vertrags-Schicht | `Heimdall.Abstractions` ist das **einzige** Paket, das Verbraucher (auch Walhalla) referenzieren | bricht die Zirkelabhängigkeit Walhalla↔Heimdall.Storage; saubere Schnittstelle für alle Projekte |
| Einbindung | .NET-Bibliothek + optionale ASP.NET-Core-Erweiterung | ein `AddHeimdall()` / `MapHeimdall()`, auch aus F# nutzbar |
| UI embedded | **Blazor server-rendered** (Komponentenbibliothek) | eine Assembly, keine Node-Toolchain im eingebetteten Fall |
| UI öffentlich | **Svelte-5 SPI** gegen REST-API | wenn APIs nach außen sichtbar werden (entspricht VectorStore-Muster) |
| Signale | Traces, Logs, Metriken (voll) | alle drei OTel-Signale; Counter/Sum/Gauge **und** Histogramm (Buckets + Sum/Count/Min/Max) |

## 3. Schichten

Drei Ingestion-Pfade laufen **in dieselbe Pipeline** — beim Embedding braucht es
keinen Netzwerk-Umweg:

```
                         ┌─────────────────────────────────────────────────────────┐
   Verbraucher            │ Heimdall.Abstractions  (VERTRAG — interfaces + Model +    │
   (API-Plattform,        │   noop-Default; KEINE Abhängigkeiten)                    │
   SQL-DB, … und          │   IHeimdallTracer / IHeimdallLogger / IHeimdallMeter /    │
   WALHALLA SELBST)       │   IHeimdallSpan / IHeimdallSink                          │
   referenzieren NUR dies └────────────┬────────────────────────────────────────────┘
                                       │ (implementiert durch, je nach Pfad)
            ┌──────────────────────────┼───────────────────────────┐
            ▼                          ▼                           ▼
(A) Heimdall.Sdk            (B) Heimdall.Direct         (C) Heimdall.Otlp / .Grpc
  OTel-Exporter (in-proc)    native API (F#)              OTLP-Empfänger (fremde Prozesse)
            │                          │                           │
            └──────────────┬───────────┴───────────────────────────┘
                           ▼  (Heimdall.Model: HSpan/HLogRecord/HMetricPoint)
              Heimdall.Ingest  (Batcher + Backpressure + Rekursionsschutz)
                           ▼
              Heimdall.Storage.Walhalla  (WalhallaEngine embedded, separater otel.db)
                           ▼
              Heimdall.Query  ─► Heimdall.Blazor (embedded)
                             ─► Heimdall.Api (SPI) ─► Heimdall.Svelte (optional)
                           ▼
              Heimdall.Embedded  (Host: AddHeimdall / MapHeimdall / HeimdallHost)
```

**Abhängigkeitsrichtung (Zirkelbruch):**
`Heimdall.Abstractions` → (nichts). `Heimdall.Direct/Sdk/Otlp/Storage/...` →
`Heimdall.Abstractions`. `WalhallaSql` → **nur** `Heimdall.Abstractions`. Umgekehrt
referenziert nur `Heimdall.Storage.Walhalla` → `WalhallaSql`. Damit zeigt **keine**
Kante von Walhalla auf die Heimdall-Storage-Implementierung.

### 3.0 Ingestion-Pfade

**(A) Heimdall.Sdk — In-Process-Exporter (kein Netzwerk).**
Apps, die ohnehin das OTel-SDK nutzen, ersetzen nur den OTLP-Exporter durch den
Heimdall-Exporter. Daten gehen direkt, in-process, in `Heimdall.Ingest`:

```csharp
builder.Services.AddOpenTelemetry()
    .UseHeimdallExporter();        // statt UseOtlpExporter() — kein HTTP, kein gRPC
```

Implementiert als `BaseExporter<Activity>` / `BaseExporter<Metric>` /
`BaseExporter<LogRecord>` (Batched), schreibt `Heimdall.Model`-Records.
Nutzt die bereits vorhandene SDK-Instrumentation (ASP.NET Core, HttpClient, …)
unverändert weiter — nur das Export-Ziel wechselt.

**(B) Heimdall.Direct — native API (ohne OTel-SDK).**
Für Apps, die kein OTel-SDK einbinden wollen (z. B. F#-Services, Bibliotheken,
Tests). Direkte, schlanke Oberfläche, kein ActivitySource/Meter-Drumherum:

```fsharp
use tracer = Heimdall.Tracer.Create("shop")
use span = tracer.StartSpan("checkout")
span.SetAttr("user", userId)
span.AddEvent("payment-authorized")
// Logs:
Heimdall.Logger.Information("order {id} placed", orderId)
// Metriken:
let meter = Heimdall.Meter.Create("shop")
meter.CreateCounter("orders").Add(1)
```

Schreibt ebenfalls `Heimdall.Model`-Records über `Heimdall.Ingest`. Optional:
`ActivitySource`/`Meter`-Adapter, sodass bestehender `System.Diagnostics`-Code
automatisch erfasst wird (Konfigurationsschalter), ohne dass das OTel-SDK
referenziert werden muss.

**(C) Heimdall.Otlp / .Grpc — OTLP-Empfänger.**
Nur nötig, wenn **andere** Prozesse (die API-Plattform, die SQL-DB als separater
Prozess, Drittanbieter) Telemetrie liefern. Für rein eingebetteten Single-Process-
Betrieb bleibt dieser Pfad **aus** (kein Port, kein Listener).

### 3.1 Heimdall.Ingest
- Zentraler In-Process-Schreiber: nimmt Model-Records, bildet Batches, schreibt
  transaktional in `Heimdall.Storage`.
- Backpressure: begrenzte In-Process-Queue (konfigurierbar); bei Überlast Drop
  nach Policy (älteste / newest / sampled) mit Zähler-Metrik.
- **Rekursionsschutz (wichtig für Selbst-Observability):** der Schreibpfad trägt
  pro AsyncLocal-Kontext eine `IsRecording`-Guard; Aufrufe aus dem Telemetrie-
  Schreiber selbst (bzw. aus dem beobachteten Storage) werden unterdrückt, damit
  das Aufzeichnen der Telemetrie nicht selbst wieder Telemetrie erzeugt. Der
  Walhalla-Sink schreibt in eine **eigene** `WalhallaEngine`-Instanz (`otel.db`),
  getrennt vom beobachteten Datenbestand.
- Einzige Schreibstelle für alle drei Pfade → Schema und Retention zentral.

### 3.1 Heimdall.Otlp
- Endpoints: `POST /v1/traces`, `/v1/metrics`, `/v1/logs` (OTLP/HTTP).
- Dekodiert `OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest` usw.
- JSON-Variante (`Content-Type: application/json`) nach OTLP/HTTP-JSON-Schema.
- gRPC: Service aus `otlp.proto`, Package `Heimdall.Otlp.Grpc` (`TraceService`/`MetricsService`/`LogsService` `Export`-RPCs). Wird von `MapHeimdall` mitgemountet, falls `Grpc.AspNetCore` referenziert ist.
- Keine SDK-Abhängigkeit — nur die generierten Proto-Typen.
- Acknowledgement: leere `Export{...}ServiceResponse` (Collector-kompatibel).

### 3.2 Heimdall.Model
- Records: `HSpan, HLogRecord, HMetricPoint, HResource, HScope`.
- Mapper Proto → Model; Attribute als Dictionary, TimeStamps als `long` (unix-ns).
- Entkoppelt, damit ein anderes Protokoll (z. B. zukünftig OTLP/gRPC ohne Neuschreiben des Storage) andocken kann.

### 3.3 Heimdall.Storage
Embedded `WalhallaEngine` mit festem Schema. Resource/Scope dedupliziert über
Attribute-Hash. Tabellen (Skizze):

```sql
CREATE TABLE resources   (id INT PK, hash STRING, attrs_json JSON);          -- dedup
CREATE TABLE scopes      (id INT PK, name STRING, version STRING, attrs_json JSON);

CREATE TABLE spans (
  trace_id STRING, span_id STRING, parent_id STRING,
  name STRING, kind INT,
  start_unix_nano INT64, end_unix_nano INT64,
  duration_ns INT64,
  status_code INT, status_msg STRING,
  resource_id INT, scope_id INT,
  attrs_json JSON, events_json JSON, links_json JSON
);
CREATE INDEX ft_spans_name ON spans USING FULLTEXT (name);                   -- Freitextsuche

CREATE TABLE logs (
  ts_unix_nano INT64, trace_id STRING, span_id STRING,
  severity INT, severity_text STRING, body STRING,
  resource_id INT, scope_id INT, attrs_json JSON
);
CREATE INDEX ft_logs_body ON logs USING FULLTEXT (body);

CREATE TABLE metrics      (id INT PK, name STRING, unit STRING, type INT, temporality INT, resource_id INT, scope_id INT);
CREATE TABLE metric_points(metric_id INT, ts_unix_nano INT64,
                            value_double DOUBLE, count INT64, sum DOUBLE,
                            min DOUBLE, max DOUBLE,
                            bucket_counts_json JSON, explicit_bounds_json JSON, flags INT);
CREATE INDEX idx_metric_point ON metric_points (metric_id, ts_unix_nano);
```

- Schreibpfad: Batch-Inserts pro OTLP-Export (Transaktion pro Request).
- Retention: Hintergrund-Sweeper, `DELETE FROM spans WHERE start_unix_nano < ?`
  konfigurierbar (Default 7 d, 0 = unbegrenzt). Sampling der High-Cardinality
  über Attribute-Drop-Regeln optional.
- Reads laufen über parametrisierte `WalhallaEngine.Prepare()` (Plan-Cache-Hit),
  Fulltext-Suche via `to_tsvector(name) @@ to_tsquery(?)`.

### 3.4 Heimdall.Query
Safer-Query-API fürs UI (kein rohes SQL nach außen):
- `ListTraces(filter, paging)` → Trace-Gruppen (trace_id, span_count, duration, error?, first_ts, service).
- `GetTrace(traceId)` → vollständiger Span-Baum.
- `SearchLogs(text, severity, time, traceId?)` → nutzt Fulltext.
- `MetricSeries(name, from, to)` → Zeitreihe; Histogram-Buckets aggregiert.
- Latenz-Perzentile / Error-Rate in SQL oder App berechnet.

### 3.5 Heimdall.Api (die "SPI")
- REST-JSON über die Query-API: `/api/traces`, `/api/traces/{id}`, `/api/logs`,
  `/api/metrics`. Identische Form wie VectorStore (minimal API).
- Auth optional via API-Key (wie VectorStore: `X-API-Key`).
- Genau diese Oberfläche macht das Svelte-5-UI möglich, sobald APIs öffentlich sind.

### 3.6 Heimdall.Blazor
- Komponentenbibliothek (`.razor`), server-rendered.
- Seiten: Traces (Liste + Filter), Trace-Detail (Gantt/Baum), Logs (Volltextsuche),
  Metriken (Serien + Histogram), ggf. Service-Map.
- Eingebunden über `MapHeimdall("/otel")` → UI unter `/otel`, OTLP unter `/otel/v1/*`.

### 3.7 Heimdall.Svelte (optional, später)
- Svelte 5 + Vite, spricht `Heimdall.Api` an. Nur bauen, wenn die API öffentlich wird.

### 3.8 Heimdall.Embedded
- `services.AddHeimdall(opt => { opt.DataPath = "./heimdall"; opt.RetentionDays = 7; })`
- `app.MapHeimdall("/otel")` → mountet Otlp + Blazor (+ optional Api).
- Für nicht-web-Einbindung: `HeimdallHost` startet intern einen Kestrel-Listener
  (`http://127.0.0.1:0`), gibt die OTLP-URL zurück.
- **F#-freundlich:** nur Record-Typen, keine optionalen C#-Only-Features,
  einfache statische Einstiegsmethoden.

### 3.9 Walhalla Selbst-Observability (Stand-Alone-Variante der DB)
Walhalla instrumentiert **sich selbst** über das Heimdall-Interface und liefert so
eine echte Stand-Alone-Variante: die DB beobachtet ihren eigenen Betrieb und zeigt
ihn unter `/otel` an. Walhalla referenziert dabei **nur** `Heimdall.Abstractions`
— es bekommt einen `IHeimdallTracer`/`IHeimdallLogger`/`IHeimdallMeter` injiziert
(oder nutzt den `Heimdall.Noop`-Default, wenn kein Heimdall-Host aktiv ist →
Null-Overhead).

Erfasste Selbst-Telemetrie (Beispiele):

| Signal | Was | Art |
|---|---|---|
| Trace `walhalla.query` | pro ausgeführtem Statement: Parser/Planner/Executor-Phasen, Dauer | Span-Baum |
| Trace `walhalla.tx` | je Transaktion: Snapshot-Erzeugung, Commit, Conflicts | Span |
| Log | WAL-Flush, Checkpoint, Lock-Wait (`wal.lock`), Analyze-Lauf | Log |
| Metrik `walhalla.table.reads` / `.writes` | **welche Tabelle wie oft** gelesen/geschrieben | Counter (Label: table) |
| Metrik `walhalla.table.rows_scanned` | gescannte Zeilen pro Tabelle | Counter (Label: table) |
| Metrik `walhalla.query.latency` | Abfragelatenz | Histogram |
| Metrik `walhalla.plancache.{hits,misses}` | Plan-Cache-Treffer (Engine hat `PlanCacheHits/Misses` schon) | Counter |
| Metrik `walhalla.tx.{active,committed,conflicts}` | MVCC-Tx-Zustände | Gauge/Counter |
| Metrik `walhalla.wal.{flushes,bytes,fsync_ms}` | WAL-Last | Counter + Histogram |

Daraus entsteht im UI z. B. eine **Table-Heatmap** (Aufrufhäufigkeit pro Tabelle),
Query-Latenz-Perzentile, Plan-Cache-Effizienz, WAL-Throughput — alles ohne
zweiten Prozess, gespeichert im separaten `otel.db`.

**Rekursions- & Isolationssicherung:**
- Sink schreibt in eine eigene `WalhallaEngine`-Instanz (`otel.db`), getrennt vom
  beobachteten Bestand.
- `Heimdall.Ingest` unterdrückt Telemetrie aus dem eigenen Schreibpfad
  (`IsRecording`-Guard, §3.1) → kein Feedback-Loop, kein Aufblähen.
- Per Schalter deaktivierbar (`WalhallaOptions.EnableSelfTelemetry`), Default `true`
  im Stand-Alone-Host, `false`/noop, wenn nur als reine Library eingebettet.

## 4. Einbindung in die bestehenden Apps

**Variante 1 — embedded, direkt (bevorzugt für in-process):**

```csharp
// in der API-Plattform / SQL-DB, im selben Prozess
builder.Services.AddHeimdall(o => o.DataPath = "./otel");
app.MapHeimdall("/otel");                       // UI + Query-API mounten

builder.Services.AddOpenTelemetry()
    .UseHeimdallExporter();                      // in-process, kein HTTP/gRPC
// oder, ohne OTel-SDK:  Heimdall.Direct.API direkt nutzen
```

Kein OTLP-Listener, kein Port — Telemetrie läuft in-process über
`Heimdall.Sdk`/`Heimdall.Direct` → `Heimdall.Ingest` → `Heimdall.Storage`.
Die Blazor-UI hängt an der App-Kestrel unter `/otel` (oder, bei einer Nicht-Web-
App, an einem minimalen lokalen Kestrel, das `Heimdall.Embedded` bei Bedarf
startet).

**Variante 2 — fremde Prozesse sammeln (OTLP):**

```csharp
// im Sammelprozess
builder.Services.AddHeimdall(o => { o.DataPath = "./otel"; o.EnableOtlpListener = true; });
app.MapHeimdall("/otel");

// in den gesammelten Apps (eigener Prozess):
builder.Services.AddOpenTelemetry()
    .UseOtlpExporter(b => { b.Endpoint = new Uri("http://heimdall-host/otel/v1/traces");
                             b.Protocol = OtlpExportProtocol.HttpProtobuf; });
```

F# analog über den generischen Host / ASP.NET oder direkt `Heimdall.Direct`.

## 5. Was "klein" heißt
- Im rein eingebetteten Betrieb: **keine** Abhängigkeit zum OTel-Ökosystem nötig
  (`OpenTelemetry.Proto` und `OpenTelemetry.*` nur, wenn OTLP-Empfänger oder der
  SDK-Exporter-Pfad aktiv sind). Der Direktpfad (`Heimdall.Direct`) braucht nur
  Walhalla + die Heimdall-Assemblies.
- Keine externen Prozesse (kein Collector, kein Tempo/Loki/Prometheus, kein Postgres).
- Storage = Walhalla-Assembly (embedded), UI = Blazor-Assembly.
- In-Process, single deployable.

## 6. Festlegungen (Entscheidung vom 2026-07-19)

- **OTLP-Transport:** HTTP/Protobuf (+JSON) **und** gRPC → Package `Heimdall.Otlp.Grpc`.
- **Metriken:** voll — Counter/UpDownCounter/Sum/Gauge **und** Histogramm (Buckets + Sum/Count + Min/Max).
- **Produktname:** `Heimdall`.
- **Retention:** Default 7 d (0 = unbegrenzt); Attribut-Drop-Regeln optional, erst bei Bedarf.

## 7. MVP-Reihenfolge (Vorschlag)
0. **`Heimdall.Abstractions`** (Vertrag: Interfaces `IHeimdallTracer/Logger/Meter/Span`,
   Model-Records, `IHeimdallSink`, `Noop`-Default). Erst danach darf irgendetwas
   konsumieren oder implementieren.
1. Heimdall.Ingest (Batcher + Rekursionsschutz) + Heimdall.Storage.Walhalla (Schema für
   Spans/Logs, separater otel.db, Retention-Sweeper).
2. Heimdall.Direct (native API) → Traces+Logs **in-process** schreiben und in der UI sehen — der eingebettete Direktpfad steht.
3. Heimdall.Query + Heimdall.Blazor: Traces-Liste + Trace-Detail + Logs-Suche (über den Direktpfad befüllt).
4. **Walhalla Selbst-Observability:** Walhalla referenziert `Heimdall.Abstractions`,
   injiziert `IHeimdallTracer/Meter/Logger`, emits `walhalla.table.reads/writes`,
   `walhalla.query.latency`, `walhalla.plancache.*` usw. → Stand-Alone-DB beobachtet
   sich selbst unter `/otel`.
5. Heimdall.Sdk (in-process OTel-Exporter) → bestehende OTel-Instrumentation ohne Umweg auf Heimdall umleiten.
6. Metriken (Counter/Sum/Gauge/Histogramm) + Metrik-Seite, in Direct + Sdk + Storage + Walhalla.
7. Heimdall.Otlp + Heimdall.Otlp.Grpc (Empfang fremder Prozesse, HTTP & gRPC).
8. Heimdall.Api (SPI) — Voraussetzung für Svelte.
9. (optional) Svelte-5-UI.

So steht der **eingebettete Direktpfad nach Schritt 2/3**, die **Walhalla-Stand-Alone-
Self-Observability nach Schritt 4**; OTLP folgt erst, wenn fremde Prozesse dazukommen.