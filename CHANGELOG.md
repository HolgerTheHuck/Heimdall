# Changelog

Alle nennenswerten Änderungen an Heimdall werden in dieser Datei dokumentiert.

Format: [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung
folgt [Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

_(keine Änderungen seit 1.0.0)_

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