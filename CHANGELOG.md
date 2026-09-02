# Changelog

Alle nennenswerten Änderungen an Heimdall werden in dieser Datei dokumentiert.

Format: [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung
folgt [Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

### Behoben
- **Alert-Store-Falle: „Dashboard ohne Alerting" killte den gesamten Host beim
  ersten Request.** Ein Host, der nur `AddHeimdallDashboard(sink)` +
  `MapHeimdallDashboard("/otel")` aufruft (ohne `AddHeimdallAlerting`), bekam
  ab dem ersten Request auf JEDER Route einen 500: Die `/alerts`-POST-Endpoints
  deklarieren `IAlertRuleStore`/`IAlertStateStore` als Handler-Parameter, beide
  Dienste wurden aber ausschließlich in `AddHeimdallAlerting` registriert — die
  `RequestDelegateFactory` warf bei der lazy Routing-Initialisierung
  („Failure to infer one or more parameters") und riss die komplette
  Routing-Tabelle mit. Zweifach behoben: (1) `AddHeimdallDashboard` registriert
  die dateibasierten Stores + Default-`HeimdallAlertingOptions` jetzt selbst
  (`TryAddSingleton`, Verzeichnisse = Options-Defaults; eine explizite
  `AddHeimdallAlerting`-Registrierung mit eigenen Dirs gewinnt, der Evaluator
  bleibt Opt-in). (2) `MapHeimdallDashboard` wirft Fail-Fast mit
  Handlungsanweisung, wenn die Stores fehlen (Mapping ohne jede Registrierung)
  — Startzeit-Fehler statt Lazy-500. Regressionstests in
  `DashboardWithoutAlertingTests` (Minimalkonfiguration → 200 auf beliebigen
  Pfaden; Mapping ohne Stores → sofortige `InvalidOperationException`).

## [1.3.0] — 2026-09-02

### Hinzugefügt

### Hinzugefügt
- **`Heimdall:Ui:TrustedOrigins`:** Trust-Anchor-Liste für den CSRF-Same-Origin-
  Check aller zustandsändernden POST-Endpoints (Login/Logout, Dashboard-
  Import/Delete, Alert-Save/Delete, /lang). Deployment hinter einem Reverse-
  Proxy/ARR mit abweichender TLD schlägt sonst fehl: Der Browser sendet beim
  Form-POST unausweisbar Origin/Referer mit der EXTERNEN Origin, während der
  Check gegen Request.Host bzw. X-Forwarded-Host läuft — reicht der Proxy die
  externe Authority nicht durch (IIS-ARR setzt X-Forwarded-Host nicht von
  selbst), wird jeder legitime POST mit 400 „cross-origin POST rejected"
  abgewiesen. Einträge sind vollständige Origins (z. B.
  `"https://portal.example.de"`); Scheme/Host case-insensitive, Ports
  default-normalisiert. Alternativ am IIS: URL-Rewrite-Server-Variable
  `HTTP_X_FORWARDED_HOST = {HTTP_HOST}` setzen (oder ARR „Preserve host
  header"). Regressionstests in `HostTrustedOriginTests` (getruster Origin
  → 302, fremder Origin → 400).
- **Offener Ingest: `Heimdall:Auth:ApiKey` ist jetzt OPTIONAL.** Bisher warf die
  Host-Validierung bei `Enabled=true` ohne ApiKey, und die API-Pfade lieferten
  bei leerem Key pauschal 401. Jetzt: ApiKey **gesetzt** → API-Pfade (OTLP/HTTP,
  Prom-API, gRPC) verlangen den Key wie bisher; ApiKey **leer gelassen** →
  API-Pfade ohne Key offen (offener Ingest), während die UI weiterhin per
  Username/Password login-geschützt ist — für Sender (SDK-Exporter, gRPC-
  Clients) im geschützten Netz, die keinen Key mitsenden sollen. Betrifft
  `HeimdallAuthMiddleware`, das gRPC-Auth-Wiring (Host + Embedded) und die
  Host-`ValidateOptions` (ApiKey-Zwang entfernt). Neue Tests
  `HostOpenIngestTests` (OTLP/Prom ohne Key → 200, UI → Login-Redirect, UI mit
  Basic → 200).
- **Dashboard-Editor (Lossless, Panel-Ebenen):** Importierte Grafana-Dashboards
  sind jetzt in der UI editierbar, ohne das JSON anzufassen. Kern ist
  `GrafanaDashboardEditor`: Mutationen laufen auf dem ROHEN JSON (JsonNode) —
  parser-fremde Felder (datasource.uid, fieldConfig.overrides, options,
  schemaVersion, transformations, Target-format/interval) überleben jeden
  Save (Lossless-Vertrag). Routen: Edit-Hub, Panel-Neu/-Edit/-Delete, Var-
  Neu/-Edit/-Delete, JSON-Rohtag, Rename, Duplicate, Create-New. Das Editieren
  findet auf PANEL-Ebene statt (wie Grafana): Jedes gerenderte Panel trägt
  einen ✎-Edit-Link (Pfad-Key via `MatchRenderKeys`, Repeat-Kopien teilen den
  Key), der Panel-Editor zeigt Live-Vorschau des ungespeicherten Standes
  (GET-Form-Submit, nichts persistiert), Targets/Thresholds sind pro Zeile
  entfernbar, neue Panels bekommen eine freie Grid-Position vorgeschlagen
  (`SuggestNewPos`), überlappende Saves werden geblockt (Meldung mit dem
  kollidierenden Panel-Titel; „Überlappung erlauben"-Haken für Ausnahmen).
  PromQL/LogQL-Target-Textareas bekommen Syntax-Highlighting (JS-Overlay,
  progressive Enhancement). Der Parser sammelt jetzt auch die Kinder
  kollabierter Rows flacher Panels-Arrays (zuvor wurden sie weder gerendert
  noch gelistet). Neue Tests `GrafanaDashboardEditorTests` +
  `GrafanaDashboardEditPageTests` (inkl. Lossless-Roundtrip auf HTTP-Ebene).
- **Multi-Var-Combobox für Template-Variablen:** Dashboards rendern Multi-
  Template-Variablen als Checkbox-Combobox (`HeimdallServiceMultiSelect`,
  generalisiert via `FieldName`/`AllText`) statt als meterhohes natives
  `<select multiple>`; Single-Variablen bleiben natives Dropdown.
- **no-store auf allen dynamischen Antworten (`UseHeimdallNoCache`):** Ohne
  Cache-Header cachen Middle-Boxen (IIS Output Caching/ARR vor dem Host)
  Panel-/API-GETs eigenmächtig — Panels „frieren" auf alten Zeitfenstern ein.
  Zusätzlich Cache-Buster (`_=ms-Timestamp`) in den Panel-URLs und
  `cache: "no-store"` in den JS-Fetches gegen Boxen, die no-store ignorieren.
- **Service-Spalte in der Logs-Liste:** `HeimdallLogRow` kann per `ShowService`
  den `service.name` je Log-Zeile zeigen (Logs-Seite opt-in; Trace-Detail,
  Dashboard-Log-Panels unverändert). Die Discovery (`ListServiceNames`/
  `ListServiceVersions`) hat einen `includeSpans`-Parameter — die Logs-Seite
  bietet nur noch Services mit LOGS an (Trace-only Services lieferten sonst
  eine leere Auswahl); die Traces-Seite bleibt Union.
- **Trace-Root-Name/-Service in der Trace-Liste (Tempo-Semantik):**
  `TraceSummary` trägt `RootName` (Name des Spans ohne Parent, Fallback
  frühester Span) und `RootService` (dessen service.name) — die Liste zeigt
  „GET /api/…" statt gekürzter Trace-IDs; fehlende Anreicherung (altes
  Backend) fällt auf den Platzhalter zurück.
- **OTLP-Drop-Logging:** Verworfene Batches sind jetzt sichtbar — 429
  (Admission-Limiter voll), 400 (Body nicht parsebar) und Sink-Write-Fehler
  (endgültiger Verlust, der OTLP-Exporter wiederholt nicht bei 400) loggen
  mit Service/Grund statt still zu verschwinden.

### Behoben
- **Auto-Refresh wirft den User aus der Listen-Position:** Die Log-/Trace-
  Liste scrollt im eigenen Container (`.hmd-list-scroll`, `overflow:auto` +
  Sticky-Header) — `location.reload()` des Auto-Refresh stellt nur die
  Fenster-Scroll-Position wieder her, die des Listen-Containers ging verloren
  (Sprung nach oben bei jedem Refresh). Fix: `heimdall.js` sichert vor dem
  Reload die Container-Position (scrollTop/scrollLeft) in `sessionStorage`
  (Key = URL ohne `refresh`-Param) und stellt sie nach dem Load wieder her —
  nur bei identischer Query, Filter-Änderungen starten bewusst oben. Live
  verifiziert (Playwright gegen Heimdall.Host): scrollTop 9674 → 9674 nach
  Reload bei `refresh=5s`.
- **`$__rate_interval`-Floor 1m → 4m (leere Panels bei kleinen Zeiträumen):**
  Bei 15-min-Zeitraum fiel `__rate_interval` auf den alten 1m-Floor (Step
  7,5 s → 4×7,5s=30s). `rate(…[1m])` enthält bei typischer 60-s-OTLP-Export-
  Kadenz meist nur EINEN Sample — `rate()` braucht ≥ 2 → leere Serie → Panels
  zeigten „Keine Daten im Zeitraum“, obwohl der 1h-Zeitraum Daten hatte (dort
  4×30s=2m → 2–3 Samples). Fix: Floor auf 4m (Grafana-Heuristik „4×
  Scrape-Intervall“) in `GrafanaTemplating.BuiltIns` — übersteht feine Steps
  und Export-Jitter. Regressionstest `BuildRenderVars_Preset15m_…` +
  aktualisierte Asserts in `GrafanaTemplatingTests`/`GrafanaDashboardRenderTests`.

## [1.2.0] — 2026-08-24

**Stand-alone-Host über OTLP/HTTP + OTLP/gRPC validiert (e2e, inkl. Docker). Bugfix-Release: gRPC+Auth, /healthz unter Auth, SelfTelemetry-Route-Filter.**

### Hinzugefügt
- **OTLP-Sender-Sample (`samples/Heimdall.OtlpSender`):** End-to-End-CLIENT
  mit dem echten OpenTelemetry-SDK (1.16.0), erzeugt feste Signale (1 Trace/2
  Spans, 1 Counter, 1 Histogram, 3 Logs) und exportiert sie via OTLP/HTTP
  **oder** OTLP/gRPC an den Stand-alone-Host. Schalter `--protocol http|grpc`,
  `--endpoint`, `--header x-heimdall-key=…` (alternativ `OTEL_EXPORTER_OTLP_*`).
  In `Heimdall.slnx` aufgenommen. Validiert die echte Wire des Hosts statt
  Roh-Protobuf per curl.
- **`HeimdallAuthOptions.AnonymousPaths`:** konfigurierbare Ausnahmen-Liste
  (exakte Pfade), die Auth völlig umgehen — für Health-/Readiness-Proben, die
  von Compose/Kubernetes ohne Credentials gerufen werden. Der Host trägt
  `/healthz` ein.
- **Regressionstest `OtlpGrpcAuthTests`:** sichert gRPC+Auth (OK mit
  `x-heimdall-key`, `Unauthenticated` ohne/falschem Key) am TestServer-Host.

### Behoben
- **gRPC+Auth war wirkungslos (Stand-alone-Host UND Embedded):** die
  `HeimdallAuthMiddleware` wies jeden OTLP/gRPC-POST mit 401 ab (UI/Rest-Zweig:
  POST ohne Session-Cookie/Basic), weil die proto-fixierten gRPC-Pfade
  (`/opentelemetry.proto.collector.{signal}.v1.{Signal}Service/Export`) nicht
  als HTTP-API-Pfade (`{prefix}/v1/*`) erkannt werden. Der Service — und damit
  seine eigene Auth via `OtlpGrpcAuth` (`x-heimdall-key` →
  `RpcException(Unauthenticated)`) — wurde nie erreicht. Fix: gRPC-Requests
  (Content-Type `application/grpc*`) werden an den Service durchgereicht, der
  seine Auth selbst prüft (eine Cookie/Basic-HTTP-Middleware ist das falsche
  Gate für gRPC, da gRPC-Clients keine Cookies senden). Betraf HTTP+Auth nicht
  (HTTP-Pfade werden als API-Pfade erkannt) und gRPC ohne Auth nicht
  (Middleware-Passthrough bei `Enabled=false`).
- **`/healthz` redirectete unter Auth auf die Login-Seite (302):** der
  Health-Endpoint ist laut Kommentaren „immer anonymous", stand aber hinter der
  Auth-Middleware und lieferte bei aktivem Auth ein 302 auf `/otel/login` —
  was HTTP-basierte Probe-Checker fälschlich als „unhealthy" werten (Compose
  nutzt `/dev/tcp`, daher nicht blockierend). Fix via `AnonymousPaths`
  (s. o.); `/healthz` liefert jetzt 200 ohne Credentials bei intakter Auth
  für alle anderen Pfade.
- **SelfTelemetry-Route-Filter war wirkungslos (`Heimdall.Sdk`):** der
  Exporter-Filter (`ExcludeRoutePrefixes`) iterierte `Activity.Tags` — das ist
  die string-only-Sicht. Die OTel-AspNetCore-Instrumentation setzt
  `http.route` aber via der object-Überladung von `SetTag`, sodass der Tag nur
  in `Activity.TagObjects` auftaucht. `ToSpan` las korrekterweise `TagObjects`
  → der Span wurde mitsamt `http.route` gespeichert; der Filter las `Tags` →
  sah `http.route` nicht → `/otel`-Spans (`/otel/logs`,
  `/otel/dashboards/{uid}/panel/{idx:int}`) slippten als Eigentelemetrie durch,
  obwohl `SelfTelemetry:Enabled: false` bzw. `ExcludeRoutePrefixes = ["/otel"]`
  gesetzt war. Fix: `ExcludeRoute` liest jetzt `Activity.TagObjects` (wie
  `ToSpan`) und prüft neben `http.route`/`http.target`/`url.path` auch
  `aspnetmvc.route` (Heimdalls eigene Enrichment-Middleware — verlässlicher
  Fallback, falls die ASP.NET-Instrumentation `http.route` nicht setzt).
  Regressionstests in `HeimdallSdkTests` reproduzieren den Bug via
  `a.SetTag(key, (object)route)` (erzwingt die object-Überladung). Betraf nur
  den Traces-Filter; der Metrik-Filter las bereits `MetricPoint.Tags` (alle
  Tags) und war nie anfällig.

## [1.1.0] — 2026-08-24

UX-Layer über dem Audit-Release: i18n (de/en/fr), Drilldown-Landing, Listen-
Paging, a11y-Remediation, SelfTelemetry-Schalter, Lazy-Dashboard-Panels
(Shell sofort, Panels per Vanilla-JS nachgeladen). Dazu das Audit-Release
(alle 🔴/🟠/🟡-Befunde aus `AUDIT.md`, Stand 2026-08-23): Funktions-Bugs,
Security-Baseline, stille Datenverluste, AlertEvaluator-Härtung, DX-Fassade,
Betrieb/UX, Doku/Paketierung. Build sauber, alle Tests grün.

**UX-Layer**

### Hinzugefügt
- **SelfTelemetry-Schalter (`Heimdall:SelfTelemetry:Enabled`):** unterdrückt
  Heimdall-Eigentelemetrie im eingebetteten Betrieb, damit das Bedienen des
  Heimdall-UIs die zu beobachtende App nicht verrauscht. Default `false` → der
  Heimdall-Exporter verwirft Spans/Metriken der Dashboard-Routes (`/otel/*`,
  erkannt am `http.route`/`http.target`/`url.path`-Tag) und Logs der Kategorie
  `Heimdall.*` (AlertEvaluator, Kanäle, …). `true` = alles erfassen, um Heimdall
  selbst zu untersuchen. App-eigene Routes und Runtime-Metriken bleiben
  unangetastet. Siehe `README.md` → „Eigentelemetrie unterdrücken".
- **Exporter-Filteroptionen (`HeimdallExporterOptions`):** `ExcludeRoutePrefixes`
  (Spans + Metrik-Punkte nach Pfad-Prefix) und `ExcludeLogCategoryPrefixes`
  (Logs nach Kategorie-Prefix) — die Library-Grundlage des SelfTelemetry-
  Schalters, frei konfigurierbar für jeden Embedded-Nutzer.
- **Drilldown-Seite (`/drilldown`):** Sprungseite zu Traces/Logs/Metriken
  (wie Grafana „Drilldown"). Die drei Signal-Detailseiten sind aus der
  Menüleiste in diese Landing ausgelagert; die Leiste führt nur noch einen
  „Drilldown"-Tab. Aktiver Tab wird markiert, wenn man sich auf einer der drei
  Unterseiten befindet.
- **Login-Seiten-Styling:** die `LoginPage.razor` nutzt jetzt das Heimdall-
  Design-System (zentrierte Karte, Panel-Look, Akzentkante, fokus-markierte
  Felder, Primary-Button) — zuvor ungestylt (Klassen waren nicht definiert).
- **Paging für die Listen-Seiten:** Logs und Traces sind serverseitig
  mengenbegrenzt (Limit) und haben jetzt eine „neuer/älter"-Paging-Steuerung
  (`HeimdallPager`, GET-Links, kein JS), die über das `offset`-Query-Param
  blättert (Backend-`OFFSET` war schon vorhanden, nur die UI nutzte ihn nicht).
  Volle Seite = „älter" verfügbar; leere Seite jenseits Offset 0 = „keine
  weiteren". Metriken (Zeitreihe) bekommt stattdessen „mehr anzeigen", das das
  Limit verdoppelt (max 5000) — echtes Paging greift hier nicht, da der Chart
  das ganze Fenster braucht.
- **i18n (Deutsch/Englisch/Französisch):** vollständige Übersetzung der Heimdall-
  UI über einen eigenen, schlanken `IHeimdallI18n`-Service (keine .resx). Umschaltbar
  oben rechts über kleine Flaggen-Buttons (`/lang`-Endpoint setzt ein `heimdall-lang`-
  Cookie); pro Request aufgelöst aus `?lang=`-Query → Cookie → `Accept-Language` →
  Default `de`. `<html lang>` passt sich an. Alle Seiten, Komponenten und auch die
  asynchronen Alert-Benachrichtigungen (SMTP-Betreff/-Body via
  `HeimdallAlertingOptions.Language`, da der AlertEvaluator ohne HTTP-Kontext läuft)
  sind übersetzt; Webhook-Payloads bleiben maschinen-lesbar.
- **Barrierefreiheit (a11y):** durchgehende Remediation — Skip-Link zum `#main`,
  systemweiter `:focus-visible`-Ring, Tabellen mit `<caption>` + `scope`, `role=img`
  + lokalisierte `aria-label` auf allen SVG-Charts (Linie/Histogramm/Heatmap/Gauge/
  BarGauge/Pie/Wasserfall), `role=status` auf Empty-States / `role=alert` auf
  Fehler-Bannern, `aria-live`-Badge für Auto-Refresh, visuell-versteckter Tone-Text
  (`Kritisch`/`Warnung`/`OK`) als Fallback zur rein farblichen KPI-Aussage,
  `aria-hidden` auf dekorativen Emoji/Pfeilen, `aria-current`/`aria-pressed` in der
  Navigation und Zeitbereich-Steuerung.

### Geändert
- **Menüleiste:** „Dashboards" direkt hinter „Dashboard" gerückt; Traces/Logs/
  Metriken als eigene Tabs entfernt (jetzt über „Drilldown").
- **Dashboard-Überschrift dynamisch:** `Aufrufe pro Sekunde (calls/s)` war
  hartcodiert und passte nicht zu frei wählbaren Metriken (Discovery-Modus).
  Jetzt `Rate „<Metrikname>" (/s)`; Chart-Unit `/s`, KPI-Labels entsprechend
  generisch (`Rate aktuell`, `max /s`).

**Audit-Release** — alle 🔴/🟠/🟡-Befunde aus `AUDIT.md` (Komplett-Audit Stand
2026-08-23): Funktions-Bugs, Security-Baseline, stille Datenverluste,
AlertEvaluator-Härtung, DX-Fassade, Betrieb/UX, Doku/Paketierung. Build sauber,
alle Tests grün (321/321/389 auf net8/9/10).

### Hinzugefügt
- **`Heimdall.Embedded`-Paket (DX-Fassade):** `AddHeimdall(o => …)` registriert
  alle Schichten (Storage + OTLP + Prometheus + Blazor + Alerting + Ingest) in
  einem Aufruf; `MapHeimdall("/otel")` mappt alle Endpunkte; `UseHeimdall()`
  kapselt Auth-Middleware + StaticFiles + Endpoints in korrekter Reihenfolge.
  Entspricht dem `DESIGN.md`-Versprechen (ein Aufruf statt 3–4 über zwei Pakete).
  `HeimdallRegistration` gibt Sink/Query/MetricSource für direkte Nutzung zurück.
- **Health-Endpoint `/healthz`:** immer anonymous (vor Auth), liefert 200 +
  Build-Version. Compose-Healthcheck + Non-root-User + Ressourcen-Limits
  (mem 512m, cpus 1.0, user 1000:1000).
- **SQLite-Read-Entkopplung:** Read-Pfade nutzen jetzt gepoolte Verbindungen
  (`Pooling=True`) statt des globalen `_gate`-Locks — WAL wirkt jetzt tatsächlich:
  Dashboard-Queries blocken den Ingest nicht mehr und umgekehrt. Write-Pfade
  bleiben hinter `_gate` (Serialisierung der Schreiber).
- **OTLP `partial_success`-Reporting:** ExponentialHistogram/Summary werden
  verworfen, aber via `ExportMetricsPartialSuccess` gemeldet (vorher: stille
  200 OK, Legacy-Clients verloren alle Metriken ohne Signal).
- **`HeimdallHub`-Drop-Counter:** `DroppedSpans`/`DroppedLogs`/`DroppedMetrics`
  für stille Sink-Fehler (vorher: leeres catch, unsichtbar).
- **AlertEvaluator-Backpressure:** SemaphoreSlim (16) für fire-and-forget-Notify
  — begrenzt gleichzeitige in-flight Channel-Requests (SMTP/Webhook), verhindert
  Resourcen-Exhaustion bei vielen feuernden Regeln. Bei vollem Gate wird der
  Channel im Tick übersprungen + geloggt (nicht blockiert).
- **`query_range`-Punktelimit (11k):** wie Prometheus — schützt vor CPU-DoS durch
  winzige Steps über weite Fenster (z. B. step=1s über 30 d).
- **CSRF-Schutz (`CheckSameOrigin`):** Origin/Referer-Check auf den 4
  zustandsändernden POST-Endpoints (Dashboard-Import/Delete, Alert-Save/Delete).
  OWASP-konformer, JavaScript-freier Schutz für Basic-Auth-UIs.
- **OTLP/HTTP Request-Size-Limit (10 MB):** via `IRequestSizeLimitMetadata` —
  schützt vor Memory-DoS (vorher: Kestrel-Default 30 MB × Admission-Cap 32 ≈ GB-Peak).
- **Regex-Cache-Cap (256):** in `SafeRegex`, `SQLiteTelemetrySink`,
  `SQLiteTelemetrySink.MetricSource` — schützt vor Memory-DoS über viele
  einzigartige Nutzer-Patterns.
- **FTS5-Input-Sanitisierung (`SanitizeFts5`):** Sonderzeichen (`*`, `:`, `(`,
  `)`, `^`) entfernt, Doppelquotes escaped, Phrasen-Wrap — keine 500er mehr
  bei unbalancierten FTS5-Queries.
- **`heim_logs(trace_id)`-Index:** TraceId-Filter auf Logs (vorher: Full Scan
  auf der größten Tabelle).
- **Secure-by-Default-Warnung:** Host loggt beim Start eine deutliche Warnung,
  wenn `Auth:Enabled=false` (ungeschützt, nicht an 0.0.0.0 in Produktion binden).
- **`IngestBuffer` im Host verdrahtbar:** `Storage:UseIngestBuffer=true`
  aktiviert Bounded-Channel + Hintergrund-Batching (Default false — synchroner
  Pfad bleibt der bewährte Default; Buffer für High-Throughput-Szenarien).
- **Zeitzone-Offset (`zzz`):** `HeimdallFmt.Ts` zeigt jetzt den UTC-Offset
  (z. B. `+02:00`), damit Anwender in nicht-UTC-Server-Zonen erkennen, dass die
  Anzeige Server-lokal ist.
- **Login-Seite (Cookie-Session statt Browser-Basic-Auth-Dialog):** eigene
  `.razor`-Seite mit HTML-Formular (POST), Branding-fähig, Session-Cookie mit
  Timeout. `HeimdallSessionCookie` (signed Cookie, HMAC-SHA256 über
  `user|expiry`, HttpOnly, SameSite=Lax, Secure bei HTTPS, `SessionTimeoutHours`
  Default 12h). `HeimdallAuthMiddleware` redirectet bei fehlendem Cookie auf
  `/login` (GET), der Login-Handler validiert Credentials (zeitkonstant via
  `SecretComparer`), setzt den Cookie und redirectet auf `returnUrl`.
  Logout-Endpoint löscht den Cookie. Basic-Auth bleibt als Fallback für
  Scripting/Curl (kein Redirect für non-GET). `HeimdallAuthOptions` additiv um
  `SessionTimeoutHours`, `CookieName`, `LoginPath`, `LogoutPath`. `AddHeimdallAuth`
  registriert die Options in DI (für den Login-Handler). API-Pfade (OTLP/Prom)
  bleiben bei 401 (kein Redirect — API-Clients folgen keinen Redirects).
- **Blazor-Projektstruktur aufgeräumt:** `Pages/` (8 Seiten), `Components/`
  (8 UI-Komponenten), `src/` (8 Source-Dateien) — vorher lagen alle Dateien
  im Root. `@namespace Heimdall.Blazor` in alle Pages/Components (Namespace
  bleibt Root — keine using-Änderungen nötig).

### Geändert
- **`MaxBytes`-Default 5 GB im Host:** Plattenfüller-Schutz bei offenem Ingest
  (vorher: 0 = unbegrenzt). Explizit `0` gesetzt = unbegrenzt. Lib-Default
  bleibt 0 (Vertragskompatibilität).
- **`docker-compose.yml` Auth-Default on:** `Heimdall__Auth__Enabled=true` +
  `change-me`-Defaults (vorher: Auth auskommentiert). Vor erstem `up` echte
  Werte setzen; Development/Demo: vier Zeilen auskommentieren.
- **AlertEvaluator: Disable einer Firing-Regel sendet `Resolved`:** vorher
  wurde Resolved verschluckt — Empfänger hingen im Alarm. Jetzt notifyen
  Firing-Regeln beim Deaktivieren (Pending-Regeln bleiben Ok ohne Notify).
- **AlertEvaluator: `EvalIntervalSeconds` pro Regel wird ausgewertet:**
  Skip-Logik im Tick (vorher: dokumentiert, aber implementierungslos — tote
  Editor-Konfiguration).
- **AlertEvaluator: Multi-Serie-Metric feuert wenn irgendeine Serie feuert:**
  Value = Maximum (vorher: nur `samples[0]` ausgewertet — Reihenfolge entschied).
- **AlertEvaluator: `StopAsync` wartet auf laufenden Tick:** mit 5s-Timeout
  (vorher: Timer gestoppt, Tick konnte noch im Flight laufen).
- **`ObservedTime`-Fallback für Logs:** `OtlpConvert.ToLog` fällt auf
  `ObservedTimeUnixNano` bzw. `now` zurück, wenn `TimeUnixNano=0` (OTel-Spec;
  vorher: Logs bei ts=0, im Default-Zeitfenster unsichtbar).
- **`Paketierung`:** `GenerateDocumentationFile` zentral aktiviert (vorher: 6
  von 11 Paketen ohne XML-Docs, inkl. `Abstractions`). `Authors` +
  `PackageReleaseNotes` zentral gesetzt. `WarningsAsErrors=nullable` als
  Quality-Gate.
- **`global.json`:** auf installiertes SDK 10.0.400 + `allowPrerelease=false`
  (vorher: 10.0.302 nicht installiert, `allowPrerelease=true` inkonsistent).
- **Versionsdrift 1.0.0 → 1.0.2:** im `README.md` behoben (Build 1.0.2,
  README/Badges 1.0.0).

### Entfernt
- **`IngestOptions.FlushIntervalMs` / `FlushWorkers`:** Phantom-Optionen, die
  nie implementiert waren (der Buffer ist drain-basiert, kein Timer/Worker-
  Pool). `BREAKING` für Code, der die Properties gesetzt hat — kein Verhalten
  ändert sich, da die Optionen keine Wirkung hatten. `IngestOptions` behält
  `MaxQueueItems`/`BatchSpans`/`BatchLogs`/`BatchMetrics`/`DropPolicy`.

### Behoben
- **Traces-Filterformular submitet zur falschen Route:** `action="@BasePath"`
  statt `/traces` — Filter-Ergebnis ging verloren.
- **Grafana-Import verliert Panels in kollabierten Rows:** `rows[].panels`
  wird jetzt rekursiv gelesen (`CollectPanelsFromRows`) — typische Community-
  Dashboards verlieren nicht mehr den Großteil der Inhalte.
- **POST `/api/v1/query` ignoriert Form-Body:** Prom-konforme POST-Clients
  (Grafana POST-Setting) bekamen 400 „query is required" — jetzt wird
  Query-String + Form-Body gelesen.
- **Endpoints→Traces-Drilldown-Link:** nutzt falschen Param (`nameContains`
  statt `name`) + falsche Route — jetzt korrekt `/traces?name=…`.
- **`(trace_id, span_id)`-Unique:** Retries/Re-Exports erzeugten Duplikatzeilen
  — UNIQUE-Index + tolerante `DeduplicateSpans`-Migration + `INSERT OR IGNORE`.
- **`LIKE` ohne `ESCAPE '\'`:** Escaping wirkungslos (nur breiterer Match) —
  jetzt mit `ESCAPE`-Klausel.
- **Chinesische Fragmente:** in `Otlp.Proto.csproj`-Description („消费") und
  `HeimdallHub.cs`-Kommentar bereinigt (landeten auf nuget.org).

### Dokumentation
- **`DESIGN.md`:** Alerting-Split-Dokumentation (verschoben, UI-Extraktion
  nötig — Alert-Pages nutzen `HeimdallNav`/`Head`/`Footer` aus `Heimdall.Blazor`,
  Zirkel ohne vorheriges `Heimdall.Ui`-Paket).
- **`host/README.md`:** Walhalla-Referenzen entfernt (`Storage:Durable`,
  `Storage:SelfObservability`, `Backend: walhalla`), `MaxBytes`-Default
  dokumentiert.
- **`grafana/README.md`:** SelfHost-Pfad → `host/Heimdall.Host`.
- **`NOTICE`:** Third-Party-Liste ergänzt (Google.Protobuf BSD-3-Clause,
  Microsoft.Data.Sqlite MIT, SQLite Public Domain).

## [1.0.2] — 2026-08-22

### Hinzugefügt
- **Embedded-Auth (opt-in):** Eingebettetes Heimdall lässt sich hinter einen
  Name/Passwort-Login verbergen (`Heimdall:Auth` in `appsettings.json`,
  `Enabled` default false = Zero-Overhead-Passthrough). HTTP Basic-Auth
  (browser-nativ, no-JS, konsistent mit dem statischen SSR).
- **Auth-Middleware in `Heimdall.AspNetCore` gehoben:** Stand-alone-Host UND
  eingebettete Apps nutzen jetzt dieselbe `UseHeimdallAuth(app,
  HeimdallAuthOptions)` (früher host-lokal, gekoppelt an `HeimdallHostOptions`).
  `HeimdallAuthOptions` additiv um `Username`, `ProtectedPrefix`,
  `OtlpHttpPrefix`/`PrometheusPrefix` und `Validate()`.
- **`ProtectedPrefix` (Embedded):** nur die Heimdall-Oberfläche (`/otel`) wird
  geschützt; App-eigene Routes (`/api/…`) bleiben frei. Host setzt null = global.
- **Dashboard-/Metrics-Discovery:** statt des hartkodierten `orders`-Beispiel-
  Defaults listet die Seite bei leerem Metrik-Namen die im Zeitraum verfügbaren
  Metrik-Namen als anklickbare Links.
- **Grafana-Built-in `$__range`:** neu in `GrafanaTemplating.BuiltIns` (neben
  `$__interval`/`$__rate_interval`). Fehlte bisher → Stat-/Table-Panels mit
  `increase(…[$__range])` krachten mit `unexpected character '$'` (z. B.
  gnetId-19924, ASP.NET Core: 6 von 10 Panels).

### Geändert
- **Username case-insensitiv:** „Admin" == „admin" beim Basic-Auth-Login
  (Usernamen merkt man sich ohne exakte Groß-/Kleinschreibung). Passwort
  bleibt case-sensitiv. Vergleiche weiterhin zeitkonstant
  (`SecretComparer`).
- **`SQLiteTelemetrySink.MetricSeries`:** leerer/fehlender Name liefert nun
  eine leere Serie statt `InvalidOperationException: Value must be set`
  (früher ausgelöst durch `Param("@n", null)` beim Dashboard-Klick mit
  leerem Errors-Counter).

### Tests
- 320 (net8/9) / 384 (net10) grün. Neu: `EmbeddedAuthTests` (6 → 10 mit
  case-insensitivem Username + case-sensitivem Passwort),
  `DashboardSeite_OhneRequests_ListetVerfuegbareMetrikNamen`,
  `MetricSeries_LeererName_LiefertLeer_StattZuWerfen`,
  `Interpolate_BuiltInRange_WirdErsetzt` + `BuiltIns_*`.

## [1.0.1] — 2026-08-22

Cleaner Nachfolger von 1.0.0. Funktional identisch; behebt ein
Dokumentations-/Metadaten-Leck des 1.0.0-Release.

### Geändert
- **README: lokale Maschinenpfade entfernt.** Die 1.0.0-Pakete betteten
  die Root-README ein (`PackageReadmeFile`), die noch lokale Dev-Pfade
  (`D:/Own/Telnet`, `cd D:/Own/Telnet`) enthielt — sichtbar auf den
  nuget.org-Paketseiten. Ersetzt durch portable Platzhalter
  (`cd Heimdall`, `<HEIMDALL-REPO>/artifacts/nupkg`).
- **`release.yml`:** Release-Notes werden jetzt versionsspezifisch passend
  zum Tag (`${GITHUB_REF_NAME#v}`) extrahiert, nicht mehr hartkodiert auf
  `[1.0.0]`.

### Hinweis
- Das ASP.NET-Core-Paket erscheint unter der ID
  **`Heimdall.AspNetCore.Enrichment`** (Namespace bleibt
  `Heimdall.AspNetCore`), da `Heimdall.AspNetCore` auf nuget.org fremd
  belegt ist. Gilt bereits ab 1.0.0; hier nachgetragen dokumentiert.

## [1.0.0] — 2026-08-22

Erste öffentliche Veröffentlichung. SQLite-only, keine Cross-Repo-Abhängigkeiten.
Walhalla-Backend vorausliegend, kehrt als NuGet-Konsument nach 1.0 zurück
(siehe [DESIGN.md](DESIGN.md)). Erscheint auf nuget.org (11 Pakete) und als
GitHub-Release; kein Code-Signing (Entscheidung #5). Repo wird mit 1.0 public.

### Hinzugefügt
- **CI-Workflow** (GitHub Actions `build.yml`): `dotnet build` + `dotnet test`
  auf Windows und Linux, .NET 8/9/10 — bei Push/PR auf `main`.
- **Apache-2.0-Lizenz** (`LICENSE` + `NOTICE`); `PackageLicenseExpression` zentral
  in `Directory.Build.props`.
- **`SECURITY.md`** (vertraulicher Meldeweg) und **`CHANGELOG.md`**.
- **Deterministische Builds + SourceLink** (`Deterministic`,
  `ContinuousIntegrationBuild`, `PublishRepositoryUrl`, `EmbedUntrackedSources`,
  `Microsoft.SourceLink.GitHub`) — Zukunftssicherung für die öffentliche 1.0.
- **Testprojekt multi-targetet** auf `net8.0;net9.0;net10.0`; Host-Boot-Tests
  via `#if NET10_0` auf net10 beschränkt, Mvc.Testing/Grpc/Host-Bezug bedingt.
- Workstream A — Per-Signal-Retention (TTL pro Signal), Größenbegrenzung
  (`MaxBytes`), Space-Reclaim (`auto_vacuum=INCREMENTAL` + VACUUM-Migration),
  Retention- & Eviction-Observability (`heimdall.retention.*`/`heimdall.storage.*`).
- Workstream F — Metriken-Rollup (zwei-stufig raw + 1m, Opt-In, all-in-Sink):
  rohe Metrik-Punkte älter als `RawDays` werden zu 1-Min-Buckets aggregiert
  statt hart gelöscht.
- Workstream B — NuGet-Packaging: `dotnet pack Heimdall.slnx` erzeugt 11
  `1.0.0`-Pakete (Lizenz-Expression, Root-README-Embed, Repository+Commit via
  SourceLink, Tags). **1.0 erscheint auf nuget.org UND als GitHub-Release**
  (Entscheidung #5 revidiert; kein Code-Signing).
- Workstream C — Operative Härtung:
  - **Admission Control (C1)** auf den OTLP-Empfängern (HTTP + gRPC):
    Concurrency-Cap (`MaxConcurrentRequests`, Default 32, `0`=unbegrenzt);
    Überlauf → HTTP 429 / `StatusCode.ResourceExhausted`. Config
    `Heimdall:Otlp:{Http,Grpc}:MaxConcurrentRequests`.
  - **`heimdall.host.*`-Self-Observability (C3):** Ingest-Counter pro Signal
    (`heimdall.host.ingest`, Prom `*_total`) + Sweep-Latenz
    (`heimdall.host.sweep.duration`, Prom `*_seconds`); synthetisiert, nicht
    in `heim_metrics` gespeichert.
  - **`SecretComparer` (C2)** — zeitkonstanter Secret-Vergleich
    (`CryptographicOperations.FixedTimeEquals`) für API-Key + Basic-Auth.
- Workstream E — **SQLitePCLRaw-Fix:** `Microsoft.Data.Sqlite` 8.0.11 → 8.0.30
  (zieht `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 / SQLite 3.53.3) schließt
  CVE-2025-6965 / GHSA-2m69-gcr7-jv3q / NU1903 (SQLite < 3.50.2, Memory-Corruption).
- Workstream E — **Release-Workflow** (`release.yml`, tag-getriggert auf `v*`):
  packt 11 × `1.0.0.nupkg`, pusht sie nach nuget.org (`NUGET_API_KEY`-Secret,
  `--skip-duplicate`) und erstellt einen GitHub-Release mit den Paket-Assets +
  den aus der CHANGELOG extrahierten Release-Notes.

### Geändert
- **Zentrale Versionierung** in `Directory.Build.props` (`1.0.0`,
  `AssemblyVersion`/`FileVersion 1.0.0.0`); die per-csproj `0.1.0`-Sätze
  wurden entfernt. `Copyright`-Metadatum zentral gesetzt.
- Roadmap-Status: Workstream A und F umgesetzt; Workstream D (Public-Readiness)
  vorbereitet.
- Workstream C — **Auth-Hygiene (C2):** API-Key nur noch via Header
  `x-heimdall-key` (Query-Fallback `?key=` entfernt — Query-Strings landen in
  Access-Logs); Vergleiche zeitkonstant.
- Workstream C — **Graceful Shutdown (C4):** Sink-Dispose auf den
  `ApplicationStopped`-Hook verschoben (nach Kestrel-Drain); `SQLiteTelemetrySink.
  Dispose()` serialisiert mit Writes via `_gate`, `Write*` mit `_disposed`-
  Double-Check (Write nach Dispose = Noop). `IngestBuffer.Dispose()` draint
  vollständig (Flush-on-Shutdown).