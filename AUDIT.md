# Heimdall — Komplett-Audit

**Stand:** 2026-08-23 · **Build-Version:** 1.0.2 · **Umfang:** alle 11 src-Projekte, Host, Tests, Samples, CI, Doku

Bewertungsskala: 🔴 kritisch · 🟠 wichtig · 🟡 mittel · ⚪ klein/kosmetisch

---

## 1. Gesamtbild

Heimdall ist ein **überdurchschnittlich sauberes Projekt**: zyklenfreier Abhängigkeitsgraph um ein
dependency-freies `Heimdall.Abstractions`, konsistente Nullable-Disziplin, kein `.Result`/`.Wait()`
im Produktionscode, nahezu null TODO-Debt, pipeline-integriertes CHANGELOG, komplette CI mit
Release-Pipeline, echte Implementierungen statt Mocks in den Tests und eine UI mit vorbildlichen
Empty-States.

Die kritischen Befunde liegen **nicht** in der Code-Hygiene, sondern in vier Clustern:

1. **Security-Baseline**: Default ohne Auth auf `0.0.0.0`, CSRF auf zustandsändernden POSTs,
   unbegrenzte PromQL-`query_range`-Iterationen.
2. **Stille Datenverluste im Ingest** (ExponentialHistogram/Summary-Drop ohne partialSuccess,
   Logs ohne Timestamp bei ts=0, Duplikate bei Retries, verschluckte Sink-Fehler ohne Counter).
3. **Einzelverbindung + globaler Lock** serialisiert alles — WAL ist wirkungslos, der
   Retention-Sweep blockt den Ingest sekundenlang.
4. **Doku-/API-Versprechen vs. Realität**: `AddHeimdall()` aus DESIGN.md existiert nicht,
   `IngestBuffer` ist in keinem Produktionspfad verdrahtet, Phantom-Optionen, Versionsdrift,
   stale READMEs.

---

## 2. Architektur & Code-Qualität

### Struktur ✅
- Stern um `Heimdall.Abstractions`; keine Zyklen; Blazor kennt nur `IHeimdallQuery`/`IHeimdallMetricSource` — keine Storage-Kopplung.
- Multi-Targeting net8/9/10 konsistent; Host/Samples net10-only.
- `Directory.Build.props`: Nullable enable, deterministisch + SourceLink, Apache-2.0, zentrale Version.
  - 🟠 `TreatWarningsAsErrors=false` — mindestens `WarningsAsErrors=nullable` wäre billiges Quality-Gate.
  - ⚪ `global.json` mit `allowPrerelease: true` inkonsistent zum Reife-Ziel.

### Befunde
| Grad | Befund | Stelle |
|---|---|---|
| 🟠 | `IngestBuffer.Flush(TimeSpan timeout)` — `timeout` wird nie verwendet (API-Lüge) | `IngestBuffer.cs:172` |
| 🟠 | Dispose-Cutoff 5 s verwirft Restdaten still; Magic Number, nicht konfigurierbar | `IngestBuffer.cs:196–203` |
| 🟠 | Stiller Totalverlust ohne Drop-Counter in `SafeWrite` | `HeimdallHub.cs:75` |
| 🟠 | AlertEvaluator: Stop joiniert laufenden Tick nicht; Fire-and-forget-Notify ohne Cancellation/Backpressure; Metric-Regeln werten nur Serie[0] aus | `AlertEvaluator.cs:73–76, 139–141, 246–253` |
| 🟡 | `GrafanaPanelRenderer` (598 Z.) mischt Rendering + LogQL-Matching + Heatmap-Mathematik | `GrafanaPanelRenderer.cs` |
| 🟡 | Limit-Kaskade 500→Filter→Cap 100 schneidet **vor** dem Match ab → TruncatedCount kann lügen | `GrafanaPanelRenderer.cs:456–462` |
| 🟡 | DashboardPage rendert komplett synchron in `OnInitialized` | `DashboardPage.razor:198–272` |
| ⚪ | Chinesische Zeichenfragmente in Kommentaren und Package-Description („消费") — landet auf nuget.org | `Otlp.Proto.csproj`, `HeimdallHub.cs:87` |
| ⚪ | 26 verschluckte Exceptions in 13 Dateien — Muster konsistent, aber teils ohne Zähler/Log | diverse |

### Design vs. Implementierung
- `AddHeimdall()`/`MapHeimdall()` aus DESIGN.md **existieren nicht** — Einbetten erfordert 3–4 Aufrufe über zwei Pakete. Größte DX-Diskrepanz für das „Öffnen". 🟠
- `Heimdall.Blazor` ist Sammelprojekt: UI + PromQL-Rendering + LogQL + **Alerting-Engine (IHostedService)**. Wer nur das Dashboard will, zieht Alerting transitiv. Aufspaltung (mindestens `Heimdall.Alerting`) **vor** nuget.org-Publikation erwägen — danach Breaking Change. 🟠
- Prometheus/Grafana-Import/Alerting fehlen als Kapitel im DESIGN.md. 🟡
- NuGet-ID `Heimdall.AspNetCore.Enrichment` vs. Namespace `Heimdall.AspNetCore` — verwirrend für Konsumenten. ⚪

---

## 3. Security

### Auth-Modell
- **Default OFFEN** (`Heimdall.Auth.Enabled=false`): UI, OTLP/HTTP+gRPC, Prom-API, Dashboard-Import/Delete, Alert-Save/Delete sind ungeschützt; Host bindet auf `0.0.0.0`. 🔴
- Bei `Enabled=true`: korrekt umgesetzt — zeitkonstante Vergleiche via `CryptographicOperations.FixedTimeEquals` (`SecretComparer.cs`), Key nur via Header (kein Query-Leak), Basic-Auth case-insensitive timing-safe, gRPC-Inline-Check konsistent, Reihenfolge im Host korrekt (auch statische Assets geschützt), leerer ApiKey → API dauerhaft 401 (konservativ-korrekt).

### Befunde
| Grad | Befund | Stelle |
|---|---|---|
| 🔴 | Default ohne Auth inkl. zustandsändernder Operationen, Ports publiziert in Compose | `HeimdallAuthOptions.cs:26`, `docker-compose.yml:19–25` |
| 🔴 | `query_range`-CPU-DoS: `(end-start)/step` Iterationen unbegrenzt (kein Punktelimit wie Prom ~11k) | `PromHttpHandlers.cs:36–53`, `PromQLEval.cs:42–58` |
| 🟠 | CSRF: 4 zustandsändernde POST-Endpoints ohne Anti-Forgery; Basic-Auth-Credentials werden cross-site automatisch mitgesendet | `HeimdallEndpointExtensions.cs:139,167,196,235` |
| 🟠 | OTLP/HTTP ohne explizites Request-Limit; JSON-Pfad liest Body als UTF-16-String (Kestrel-Default 30 MB × Admission-Cap 32 ≈ GB-Peak) | `OtlpEndpointExtensions.cs:96–120` |
| 🟠 | Unbegrenzte Regex-Caches über Nutzereingaben (Memory-DoS); Match-Timeouts decken Compile nicht | `SQLiteTelemetrySink.cs:33,60–72`, `SafeRegex.cs:14–40` |
| 🟠 | `MaxBytes` default 0 = unbegrenzte DB | `SQLiteTelemetryOptions.cs:32–37` |
| 🟠 | Keine Security-Headers (nosniff, X-Frame-Options, CSP); HSTS/TLS-Thema undokumentiert | gesamt |
| 🟡 | Kein Brute-Force-Schutz (Delay/Lockout) auf Auth | `HeimdallAuthMiddleware.cs` |
| 🟡 | FTS5-Userinput ungeprüft an `MATCH` → Sonderzeichen werfen 500er | `SQLiteTelemetrySink.cs` |
| ⚪ | `LIKE` ohne `ESCAPE '\'` — Escaping wirkungslos (nur breiterer Match) | `SQLiteTelemetrySink.cs:236–240` |

### Positiv geprüft ✅
SQL durchgängig parametrisiert (keine Injection); XSS: Razor-Encoding überall, SVG-Strings escapen dynamische Anteile, Chart-JSON korrekt verpackt, JS-Tooltips escapen; Secrets-frei (appsettings/compose/samples); Path-Traversal via Zeichen-Whitelist in beiden File-Stores abgedeckt; Webhook-URL nur Operator-kontrolliert (kein nutzer-SSRF); CORS default zu.

---

## 4. Funktionalität & Korrektheit

### Bugs (wirken kaputt)
| Grad | Befund | Stelle |
|---|---|---|
| 🔴 | **Traces-Filterformular submitet zur falschen Route** (`action="@BasePath"` statt `/traces`) — Filter-Ergebnis geht verloren | `TracesPage.razor:14` |
| 🔴 | **Grafana-Import verliert Panels in kollabierten Rows** (`row.panels` wird nicht rekursiv gelesen) — typische Community-Dashboards verlieren den Großteil der Inhalte, ohne Warnung | `GrafanaDashboardModel.ParseRoot` |
| 🔴 | **POST `/api/v1/query` ignoriert Form-Body** — Prom-konforme POST-Clients (Grafana POST-Setting!) bekommen 400 „query is required" | `PromEndpointExtensions.cs:35–36` |
| 🟠 | Per-Regel-`EvalIntervalSeconds` wird nirgends ausgewertet — tote Editor-Konfiguration | `AlertEvaluator.cs` |
| 🟠 | Endpoints→Traces-Drilldown-Link nutzt falschen Param (`nameContains` statt `name`) | `EndpointsPage.razor` |
| 🟠 | Deaktivieren einer Firing-Regel benachrichtigt kein Resolved — Empfänger hängen im Alarm | `AlertEvaluator.ProcessRule` |

### Stille Datenverluste / Semantik
| Grad | Befund | Stelle |
|---|---|---|
| 🟠 | ExponentialHistogram + Summary werden verworfen (**200 OK**, kein `partialSuccess`) — Legacy-Clients verlieren alle Metriken | `OtlpConvert.cs:233` |
| 🟠 | Logs ohne `TimeUnixNano` landen bei ts=0 (kein ObservedTime-Fallback, OTel-Spec-Verstoß) | `OtlpConvert.cs:150–163` |
| 🟠 | Kein Unique auf `(trace_id, span_id)` — Retries erzeugen Duplikatzeilen | Schema `heim_spans` |
| 🟠 | `FetchRealPoints`: SQL-LIMIT greift **vor** dem In-App-Matcher → zu wenige/leere Ergebnisse trotz Treffern | `SQLiteTelemetrySink.MetricSource.cs:157–176` |
| 🟡 | Histogram ohne `HasSum` schreibt Sum=0 statt NULL (nicht unterscheidbar) | `OtlpConvert.cs:216–222` |
| 🟡 | `delta()` als Counter-Increase implementiert — falsche Werte für Gauges | `PromQLFunctions.cs:53` |
| 🟡 | Subqueries `[5m:1m]` geparst, aber funktionsunfähig als Instant-Vektor-Argument | `PromQLParser.cs:233–238` |
| 🟡 | `MetricSeries` ohne Downsampling: ASC+LIMIT schneidet die **neuesten** Punkte ab | SQLiteTelemetrySink |
| 🟡 | Zukunfts-Timestamps/Clock-Skew: Default-Fenster enden bei now → Daten unsichtbar | HeimdallRange |
| ⚪ | `histogram_sum/avg` NaN-Stubs; `rate/increase` ohne Extrapolation; `/labels,/series` ignorieren `match[]`; fixer 5-min-Lookback | Prometheus |

### PromQL-Abdeckung
Breit unterstützt (rate/increase/irate, Aggregationen mit by/without, binäre Ops + on/ignoring/group_left/right, Set-Ops, label_replace, histogram_quantile, offset, @, bool). Fehlend: Subqueries, `@ start()/end()`, Extrapolation, einige Metadata-Endpoints.

### Alerting ✅ (mit Einschränkungen)
Zustandsautomat Ok→Pending→Firing→Resolved sauber, Restart-Persistenz funktioniert (kein Re-Notify-Spam), Reentrancy-Guard korrekt.

---

## 5. Performance & Skalierung

| Grad | Befund | Stelle |
|---|---|---|
| 🔴 | **Eine SQLite-Verbindung + globaler `_gate`-Lock serialisiert ALLE Reads und Writes** — WAL bringt nichts (nur bei Multi-Connection wirksam); Dashboard-Queries blocken Ingest und umgekehrt; Sweep hält den Lock über FTS-Rebuild+VACUUM (Sekunden) | `SQLiteTelemetrySink.cs:44, 564ff` |
| 🟠 | **`IngestBuffer` (Backpressure/Batching) in keinem Produktionspfad verdrahtet** — OTLP-Receiver und SDK-Exporter schreiben synchron direkt in den Sink; Embedded-Pfad hat gar keinen Überlaufschutz | grep: nur Tests/Doku |
| 🟠 | Phantom-Optionen: `FlushIntervalMs`/`FlushWorkers` dokumentiert, implementierungslos | `IngestOptions.cs` |
| 🟡 | Fehlende Indizes: `heim_logs(trace_id)` (TraceId-Filter = Full Scan auf größter Tabelle), Service-Filter via `LIKE '%…%'` auf resource_json | `BootstrapSchema` |
| 🟡 | Kein SQLite-Tuning (cache_size/mmap_size/temp_store), nicht konfigurierbar | `SQLiteTelemetrySink.cs:88–95` |
| ⚪ | `resource_json` pro Zeile redundant gespeichert (kein Dedup); Label-Discovery scannt bis 50k Zeilen pro `/api/v1/labels` | — |

Positiv: WAL+synchronous=NORMAL+incremental_vacuum korrekt gesetzt, Batch-Inserts vorbereitet (kein N+1), Channel-Kapazitäten konfigurierbar, Drop-Counter vorhanden.

---

## 6. Betrieb & Deployment

| Aspekt | Status |
|---|---|
| Health-Check | ❌ keiner (`/healthz` fehlt) → Compose ohne Healthcheck, keine Probes |
| Graceful Shutdown | ✅ sauber (Sink-Dispose nach Kestrel-Drain, getestet) |
| Options-Validierung | ✅ Fail-fast beim Start |
| Selbst-Observability | ✅ gut (heimdall.*-Metriken; Limitation „now-only" dokumentiert) |
| docker-compose.yml | 🟡 Volume+restart ok; aber kein Healthcheck, keine Ressourcen-Limits, Container läuft als **root** |
| MaxBytes=0 Default | 🟠 unbegrenzte DB + offener Ingest = Plattenfüller |

---

## 7. Paketierung & Release

| Punkt | Status |
|---|---|
| License/Readme/SourceLink/deterministic | ✅ zentral korrekt |
| `GenerateDocumentationFile` | ❌ fehlt auf 6 von 11 Paketen — **inkl. `Abstractions`** (DER Verbrauchervertrag) |
| PackageIcon / PackageReleaseNotes / Authors | ❌ fehlen |
| Versionsdrift | ❌ Build 1.0.2 vs. README/Badges 1.0.0 |
| Paket-README | 🟡 alle 11 bekommen dieselbe lange Root-README |
| snupkg | ✅ implizit konfiguriert (beim Release mitpushen) |

---

## 8. Dokumentation

**Positiv:** Root-README beschreibt die reale API korrekt (geprüft gegen Sample-/Host-Code); CHANGELOG vorbildlich gepflegt und release-pipeline-integriert; Kommentare erklären *Warum* statt *Was*.

**Fehler (Doku ≠ Code):**
1. 🟠 `host/README.md`: Konfig-Tabelle listet `Storage:Durable`, `SelfObservability` und „Backend: sqlite oder walhalla" — Walhalla wirft in 1.0 eine Exception. Prä-1.0-Stand.
2. 🟡 `grafana/README.md` referenziert `samples/Heimdall.SelfHost` (existiert nicht mehr).
3. 🟡 `IngestOptions.FlushIntervalMs`/`FlushWorkers` dokumentieren nicht existierendes Verhalten.
4. 🟡 ExponentialHistogram/Summary-Drop steht nur in DESIGN §2, nicht in README-Limits/CHANGELOG.
5. ⚪ Root-README zeigt falschen Pfad fürs Beispiel-Dashboard; Versionsdrift 1.0.0↔1.0.2; Typos.

---

## 9. Lizenz & Compliance

- LICENSE (Apache-2.0 vollständig) + NOTICE (eigenes Copyright) ✅; `PackageLicenseExpression` zentral ✅.
- 🟡 NOTICE listet **keine Third-Party-Komponenten** — Google.Protobuf (BSD-3) und Microsoft.Data.Sqlite (MIT) erwarten Notice-Preservation. Kurze Third-Party-Liste ergänzen.

---

## 10. UI / Usability / a11y / i18n

### Stärken ✅
Deep-Linking durchgängig (GET-Parameter, Brushing schreibt History), geführte Empty-States mit OTLP-Ingest-Hinweisen (besser als viele etablierte Tools), natives `<details>` für Log-Zeilen (No-JS-tauglich), implizite Label-Assoziation überall, `aria-current`/`aria-pressed`/SVG-`role="img"+aria-label`, AA-Kontrast, Progressive Enhancement degradiert fast überall sauber.

### Top-Probleme
| Grad | Problem | Stelle |
|---|---|---|
| 🟠 | Server-Zeitzone ohne Kennzeichnung (`ToLocalTime()` ohne Offset) — bei Server≠User-Zone wirken alle Zeiten falsch | `HeimdallFmt.cs:13–15` |
| 🟠 | Custom-Zeitraum ohne JS still wirkungslos; DashboardPage zeigt rohe Nanosekunden | `HeimdallTimeRange.razor`, `DashboardPage.razor:225–228` |
| 🟠 | Keine Pagination-UI; Limit-Cap wird als Fake-Gesamtzahl angezeigt („200 Log(s)") | `TracesPage.razor:57`, `LogsPage.razor:56` |
| 🟡 | TraceId trunciert (12/16 Z.) ohne `title`/Copy-Möglichkeit | `HeimdallLogRow.razor:47` |
| 🟡 | Kein globales Fehlerhandling für Query-Exceptions → DB-Fehler = ASP.NET-Fehlerseite | alle Seiten |
| 🟡 | Detailseiten markieren keinen Nav-Tab (Orientierungsverlust beim Drilldown) | `HeimdallNav.razor:47` |
| 🟡 | A11y-Basiskette: kein Skip-Link, keine `:focus-visible`-Styles, `<th>` ohne `scope`, Row-Toggle ohne `aria-expanded`, Charts rein maus-basiert | CSS/Nav/Tabellen |
| ⚪ | Nur Dark Theme, kein Print-Stylesheet, `prefers-reduced-motion` ignoriert, breite Tabellen ohne horizontales Scroll-Handling mobil | CSS |
| ⚪ | i18n: hart deutsch (Jaeger/Grafana-Konvention ist englisch — bewusste Entscheidung wert); Zahlen invariant gut; JS-Tooltips nutzen Browser-Locale ≠ Server-Format | gesamt |

Was Grafana/Jaeger-gewöhnte Anwender vermissen: Live-Tail (Auto-Refresh = Full-Reload), Klick-auf-Attribut-als-Filter, CSV/JSON-Export, sortierbare Spalten, kopierbare IDs, Annotations auf Charts, Compare/Lookback.

---

## 11. Tests & Wartbarkeit

**Test-Suite ✅ überdurchschnittlich:** 41 Dateien, xUnit, echte Implementierungen (echtes SQLite, echte PromEngine), Integration via `WebApplicationFactory` (HostBoot/Composition/Shutdown/Auth/UI/gRPC), Multi-TFM über `#if NET10_0` sauber gegated, CI-Matrix (win+ubuntu × SDK 8/9/10) + Release-Workflow mit Pack/Push/Notes-Extraktion.

Lücken: `HeimdallCharting` (673 Z.) nur indirekt getestet; `AlertStateStore`-Persistenz/Corrupt-Recovery ohne eigene Tests; `IngestBuffer.Dispose` unter Last ungetestet; Auth-Edge-Cases (Brute-Force, leere ApiKey) dünn; kein Coverage-Reporting.

Wartbarkeit:
- 🟡 Globale Test-Serialisierung (`DisableTestParallelization=true` wegen Env-Vars) skaliert schlecht — mittelfristig Config-Override statt Env-Vars.
- 🟡 Razor-Duplikation: `RangeLabel(...)` identisch in 2 Seiten, Range-Boilerplate in 5 Seiten.
- 🟡 Storage-Backend-Hardcoding: `"sqlite"`-String-Vergleiche; externes Backend (Walhalla-Rückkehr) kein echter Extension-Point.
- ✅ Erweiterungspunkte: neuer Alert-Channel vorbildlich dokumentiert (niedrig); neues Panel-Type niedrig-mittel; neues Storage-Backend mittel-hoch (nicht offiziell begehbar).
- ✅ Genau 1 TODO im ganzen Baum (vendored Proto). Kommentarqualität hoch; Orthographie-Mix (ASCII-Umlaute vs. echt) kosmetisch.

---

## 12. Priorisierte Empfehlungen

### Vor nuget.org-Push (Blocker)
1. 🔴 Security-Baseline: Auth-Diskussion (Secure-by-Default oder Start-Warnung + Compose-Default mit Auth), `query_range`-Punktelimit (~11k), Anti-Forgery auf die 4 POST-Endpoints, Request-Size-Limit für OTLP/HTTP, Regex-Cache-Cap, Security-Headers.
2. 🔴 Funktions-Bugs fixen: Traces-Form-Action, kollabierte Rows beim Grafana-Import, POST-Form-Body bei `/api/v1/query`.
3. 🟠 Stille Datenverluste: `partialSuccess`-Reporting, ObservedTime-Fallback, `(trace_id,span_id)`-Unique oder Dedup, Drop-Counter in `SafeWrite`.
4. 🟠 Paketierung: XML-Docs auf allen Paketen (Abstractions!), Icon, ReleaseNotes aus CHANGELOG, Versionsdrift auflösen, chinesische Fragmente bereinigen, `Heimdall.Alerting`-Split entscheiden.
5. 🟠 Doku aktualisieren: host/README (Walhalla raus), grafana/README (SelfHost-Pfad), IngestOptions-Phantom-Optionen entfernen/implementieren.

### Kurzfristig (1.x)
6. 🟠 `IngestBuffer` entweder verdrahten oder ehrlich dokumentieren; Flush-Timeout-API reparieren.
7. 🟠 Zeitzone anzeigen (UTC oder Offset-Suffix); Pagination-UI (Offset-Links reichen, konsistent zum GET-Ansatz); TraceId kopierbar.
8. 🟠 Health-Endpoint + Compose-Healthcheck + Non-root-User + Ressourcen-Limits; `MaxBytes`-Default überdenken.
9. 🟡 FTS-Input sanitisieren (Exception→leeres Resultat), `ESCAPE` am LIKE, fehlende Indizes (`logs.trace_id`).
10. 🟡 A11y-Basiskette (Skip-Link, focus-visible, th scope, aria-expanded).

### Mittelfristig
11. 🟡 SQLite-Nebenläufigkeit: Read-Verbindungen vom Write-Lock trennen (WAL wirkt erst dann), Sweep entzerrten.
12. 🟡 `delta()`-Semantik, Subqueries, Extrapolation — Prom-Kompatibilität nachziehen oder dokumentieren.
13. ⚪ i18n-Entscheidung (englisch als Default?), Light-Theme, Test-Parallelisierung, Coverage.
