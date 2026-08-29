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
| `Storage:Backend` | `sqlite` | `sqlite` (1.0: SQLite-only) |
| `Storage:DataPath` | `var/heimdall/otel.db` | SQLite-Datei |
| `Storage:RetentionDays` | `7` | 0 = unbegrenzt |
| `Storage:WalMode` | `true` | SQLite WAL + foreign_keys |
| `Storage:MaxBytes` | `0` (→ Host-Default 5 GB) | Harter Plafond über die DB-Datei; 0 = Host setzt 5 GB-Default (Plattenfüller-Schutz bei offenem Ingest), explizit 0 = unbegrenzt |
| `Otlp:Http:Enabled` / `:Prefix` | `true` / `/otel` | OTLP/HTTP-Empfänger |
| `Otlp:Http:MaxConcurrentRequests` | `32` | Admission-Control-Cap (C1); 0 = unbegrenzt; Überlauf → 429 |
| `Otlp:Grpc:Enabled` | `true` | OTLP/gRPC-Empfänger (HTTP/2-Endpunkt 4317) |
| `Otlp:Grpc:MaxConcurrentRequests` | `32` | Admission-Control-Cap (C1), 3 Services teilen sich das Cap; 0 = unbegrenzt; Überlauf → `ResourceExhausted` |
| `Prometheus:Enabled` / `:Prefix` | `true` / `/otel` | PromQL-Engine + Prom-HTTP-API |
| `Dashboard:Enabled` / `:Prefix` | `true` / `/otel` | Blazor-Dashboard |
| `DashboardsStore:Dir` / `:SeedExample` | `var/heimdall/dashboards` / `false` | dateibasierter Grafana-Store |
| `Auth:Enabled` | `true` (`appsettings.Development.json`: `false`) | Minimal-Auth + Login-Screen (siehe unten) |
| `SeedDemoData` | `false` | Demo-Daten + MVC-Drilldown-Saat (rein additiv) |

Kestrel-Endpunkte (`Kestrel:Endpoints`): `http-ui` (5099, `Http1AndHttp2`) und `grpc-otlp`
(4317, `Http2`). Die pro-Endpunkt-Protokoll-Trennung ist nur via `Kestrel:Endpoints`
möglich (nicht via `ASPNETCORE_URLS`).

Env-Vars überschreiben (Docker/CI): `Heimdall__Storage__DataPath=/data/otel.db`,
`Heimdall__Auth__ApiKey=…` usw. (`__` → `:`).

## Minimal-Auth + Login-Screen

`Auth:Enabled=true` (Default im Host) schaltet scharf (Auth-Middleware in
`Heimdall.AspNetCore`, vom Host hier nur konfiguriert — dieselbe nutzt auch der
Embedded-Pfad):

- **UI / Login-Screen**: Alle Seiten außer Login/Logout verlangen eine Session.
  Unauthentifizierte Browser-Requests landen auf der **Login-Seite** (`/otel/login`,
  eigener Screen ohne Nav/Footer); nach erfolgreichem Login geht es zur ursprünglich
  angefragten Seite (`returnUrl`) bzw. zur Startseite. Logout: `POST /otel/logout`.
  Die Session steckt im Cookie `heimdall-auth` (HMAC-signiert, HttpOnly, SameSite=Lax,
  `Secure` bei HTTPS, gültig 12 h — `Auth:SessionTimeoutHours`).
- **Credentials** via `Auth:Username` / `Auth:Password` / `Auth:ApiKey`
  (IIS: alternativ Env-Vars `Heimdall__Auth__Username=…`, `Heimdall__Auth__Password=…`).
  `Username: null` = beliebiger Name gegen ein Shared-Password.
  Die appsettings-Defaults sind Platzhalter (`admin` / `change-me`) für die erste
  Inbetriebnahme — der Host warnt beim Start, solange sie gesetzt sind. Ein
  Passwortwechsel invalidiert alle laufenden Sessions (das HMAC-Secret des Cookies
  leitet sich vom Passwort ab).
- **OTLP/HTTP + Prom-API** (`{Prefix}/v1/*`, `{Prefix}/api/v1/*`): Shared API-Key via
  Header `x-heimdall-key` (**Header only — kein Query-Fallback**, da Query-Strings in
  Access-Logs/Proxies landen). OTel-SDKs: `OTEL_EXPORTER_OTLP_HEADERS="x-heimdall-key=…"`.
  Grafana-Prometheus-Datasource: Custom-Header `x-heimdall-key: …`.
- **gRPC**: Inline-Check in den Service-Implementierungen (Header `x-heimdall-key`), bei
  Fehlen `RpcException(Unauthenticated)`.
- **Basic-Auth-Fallback**: Skripte/Clients können statt des Cookies HTTP-Basic mit
  `Username`/`Password` mitschicken (autorisiert pro Request, es wird kein Cookie gesetzt).
- **`/healthz`** bleibt anonym (Compose/K8s-Proben ohne Credentials → 200).
- **Statische Web-Assets** (`/_content/Heimdall.Blazor/*`) bleiben anonym
  (`Auth:AnonymousPrefixes`) — die Login-Seite lädt ihr Stylesheet gerade ohne
  Session; app-fremde `/_content`-Pfade bleiben geschützt.

Vergleiche sind **zeitkonstant** (`SecretComparer`/`CryptographicOperations.FixedTimeEquals`).
Admission Control (C1) greift nach bestandenem Auth: über dem Cap liegende Requests
werden sofort abgewiesen (HTTP 429 / gRPC `ResourceExhausted`, retrybar).

`Auth:Enabled=false` = Zero-Overhead-Passthrough (`appsettings.Development.json` setzt das
für lokale `dotnet run`-Entwicklung; Docker-Compose aktiviert Auth per Default).

## Publish (ohne Docker)

```bash
dotnet publish host/Heimdall.Host -c Release -r linux-x64 --self-contained \
    /p:PublishSingleFile=true /p:PublishTrimmed=false
```

Trimming explizit **aus** (gRPC-Codegen + Protobuf-Reflection + Razor-Discovery sind
trim-unfreundlich). Single-File ist unkritisch. Ohne `--self-contained` ergibt ein
framework-dependent Deployment ein kleineres Paket (benötigt die .NET-10-Runtime auf dem Ziel).

## Deployment unter Pfad-Prefix (IIS-Unterverzeichnis / Reverse-Proxy)

Das Deployment als IIS-Sub-Application (z. B. Site `/` → App `/otel`) oder hinter einem
Reverse-Proxy mit Pfad-Strip funktioniert ohne Zusatzkonfiguration: alle generierten
UI-URLs tragen die `Request.PathBase` (`HeimdallUiPaths`). Konfig-Prefixe (`Otlp:Http`,
`Prometheus`, `Dashboard`, `Auth`) bleiben **in-app-relativ** — bei Sub-App `/otel` und
Prefix `/otel` liegt das Dashboard extern unter `/otel/otel`, die UI-Links und Assets
passen automatisch. Exporter/Grafana zeigen auf die externen URLs
(`http://host/otel/v1/…`, `http://host/otel/api/v1/…`).

Einschränkungen unter IIS/ANCM: gRPC (h2c, Port 4317) ist hinter IIS nicht erreichbar —
`Otlp:Grpc:Enabled=false` setzen und via OTLP/HTTP exportieren.

Der Login-Screen ist unter IIS automatisch aktiv: IIS startet die App im Production-
Environment (das Development-Override `Auth:Enabled=false` greift dort nicht). Wer die
Credentials nicht in die appsettings schreibt, setzt sie per Umgebungsvariable
(`Heimdall__Auth__Username` / `Heimdall__Auth__Password` / `Heimdall__Auth__ApiKey`).

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