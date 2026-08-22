# Changelog

Alle nennenswerten Änderungen an Heimdall werden in dieser Datei dokumentiert.

Format: [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung
folgt [Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

_(keine Änderungen seit 1.0.2)_

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