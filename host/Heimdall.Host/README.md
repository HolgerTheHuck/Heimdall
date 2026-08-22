# Heimdall.Host — Stand-alone-Backend

Config-getriebener, persistenter Heimdall-Host (Grafana-Stack-Äquivalent): Dashboard
(Blazor), OTLP-Empfänger (HTTP + gRPC), Prometheus-API und dateibasierter Dashboard-Store —
alles schaltbar via `appsettings.json` Sektion `Heimdall`. Eingebettete Nutzung bleibt
unangetastet (alle `Add*`/`Map*`-Signaturen unverändert, keine `IOptions`-Maschinerie).

## Start

```bash
dotnet run --project host/Heimdall.Host              # Development: Demo-Daten + Beispiel-Dashboard
```

- UI / OTLP-HTTP:  http://localhost:5099/otel
- OTLP/gRPC:       localhost:4317
- Prom-API:        GET http://localhost:5099/otel/api/v1/{query,query_range,labels,status/buildinfo,…}

`appsettings.Development.json` setzt `SeedDemoData=true` und `DashboardsStore.SeedExample=true`
— die DB wird dabei **nicht** gelöscht (im Gegensatz zum alten `Heimdall.SelfHost`): Restart
erhält den Bestand.

## Konfiguration (Sektion `Heimdall`)

| Pfad | Default | Bedeutung |
|------|---------|-----------|
| `Storage:Backend` | `sqlite` | `sqlite` oder `walhalla` |
| `Storage:DataPath` | `var/heimdall/otel.db` | SQLite-Datei / Walhalla-Verzeichnis |
| `Storage:RetentionDays` | `7` | 0 = unbegrenzt |
| `Storage:WalMode` | `true` | SQLite WAL + foreign_keys |
| `Storage:Durable` | `true` | Walhalla WAL-Sync (Fsync) |
| `Storage:SelfObservability` | `false` | Walhalla: otel.db-Engine instrumentiert sich selbst |
| `Otlp:Http:Enabled` / `:Prefix` | `true` / `/otel` | OTLP/HTTP-Empfänger |
| `Otlp:Http:MaxConcurrentRequests` | `32` | Admission-Control-Cap (C1); 0 = unbegrenzt; Überlauf → 429 |
| `Otlp:Grpc:Enabled` | `true` | OTLP/gRPC-Empfänger (HTTP/2-Endpunkt 4317) |
| `Otlp:Grpc:MaxConcurrentRequests` | `32` | Admission-Control-Cap (C1), 3 Services teilen sich das Cap; 0 = unbegrenzt; Überlauf → `ResourceExhausted` |
| `Prometheus:Enabled` / `:Prefix` | `true` / `/otel` | PromQL-Engine + Prom-HTTP-API |
| `Dashboard:Enabled` / `:Prefix` | `true` / `/otel` | Blazor-Dashboard |
| `DashboardsStore:Dir` / `:SeedExample` | `var/heimdall/dashboards` / `false` | dateibasierter Grafana-Store |
| `Auth:Enabled` | `false` | Minimal-Auth (siehe unten) |
| `SeedDemoData` | `false` | Demo-Daten + MVC-Drilldown-Saat (rein additiv) |

Kestrel-Endpunkte (`Kestrel:Endpoints`): `http-ui` (5099, `Http1AndHttp2`) und `grpc-otlp`
(4317, `Http2`). Die pro-Endpunkt-Protokoll-Trennung ist nur via `Kestrel:Endpoints`
möglich (nicht via `ASPNETCORE_URLS`).

Env-Vars überschreiben (Docker/CI): `Heimdall__Storage__Backend=walhalla`,
`Heimdall__Auth__ApiKey=…` usw. (`__` → `:`).

## Minimal-Auth

`Auth:Enabled=true` schaltet zwei Mechanismen scharf (Auth-Middleware in
`Heimdall.AspNetCore`, vom Host hier nur konfiguriert — dieselbe nutzt auch der
Embedded-Pfad):

- **OTLP/HTTP + Prom-API** (`{Prefix}/v1/*`, `{Prefix}/api/v1/*`): Shared API-Key via
  Header `x-heimdall-key` (**Header only — kein Query-Fallback**, da Query-Strings in
  Access-Logs/Proxies landen). OTel-SDKs: `OTEL_EXPORTER_OTLP_HEADERS="x-heimdall-key=…"`.
  Grafana-Prometheus-Datasource: Custom-Header `x-heimdall-key: …`.
- **UI / Rest**: Basic-Auth gegen `Auth:Username` (optional; null = beliebiger Name,
  Shared-Password) + `Auth:Password`.
- **gRPC**: Inline-Check in den Service-Implementierungen (Header `x-heimdall-key`), bei
  Fehlen `RpcException(Unauthenticated)`.

Vergleiche sind **zeitkonstant** (`SecretComparer`/`CryptographicOperations.FixedTimeEquals`).
Admission Control (C1) greift nach bestandenem Auth: über dem Cap liegende Requests
werden sofort abgewiesen (HTTP 429 / gRPC `ResourceExhausted`, retrybar).

`Auth:Enabled=false` (Default) = Zero-Overhead-Passthrough (Demo/Embedded unverändert).

## Publish (ohne Docker)

```bash
dotnet publish host/Heimdall.Host -c Release -r linux-x64 --self-contained \
    /p:PublishSingleFile=true /p:PublishTrimmed=false
```

Trimming explizit **aus** (gRPC-Codegen + Protobuf-Reflection + Razor-Discovery sind
trim-unfreundlich). Single-File ist unkritisch. Ohne `--self-contained` ergibt ein
framework-dependent Deployment ein kleineres Paket (benötigt die .NET-10-Runtime auf dem Ziel).

## Docker (SQLite-only)

Der Walhalla-Backend-Zweig liegt als Cross-Repo-Referenz außerhalb des Build-Contexts und
wird via `-p:IncludeWalhalla=false` ausgeblendet. `Backend=="walhalla"` wirft in diesem
Image eine `InvalidOperationException` (klare Meldung). Für Walhalla im Container muss der
Build-Context `../Walhalla` mit einschließen und ohne das Flag gebaut werden (Folge-Phase,
sobald Walhalla als NuGet-Package vorliegt).

```bash
# vom Repo-Root:
docker build -t heimdall -f host/Heimdall.Host/Dockerfile .
docker run -p 5099:5099 -p 4317:4317 -v ./var/heimdall:/app/var/heimdall heimdall
# oder:
docker compose up --build
```

Persistenz landet im Volume `./var/heimdall` (Telemetrie-DB + Dashboards).