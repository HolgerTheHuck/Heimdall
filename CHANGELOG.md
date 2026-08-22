# Changelog

Alle nennenswerten Änderungen an Heimdall werden in dieser Datei dokumentiert.

Format: [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung
folgt [Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

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

### Geändert
- **Zentrale Versionierung** in `Directory.Build.props` (`1.0.0`,
  `AssemblyVersion`/`FileVersion 1.0.0.0`); die per-csproj `0.1.0`-Sätze
  wurden entfernt. `Copyright`-Metadatum zentral gesetzt.
- Roadmap-Status: Workstream A und F umgesetzt; Workstream D (Public-Readiness)
  vorbereitet.

## [1.0.0] — TBD

Erste öffentliche Veröffentlichung. SQLite-only, keine Cross-Repo-Abhängigkeiten.
Walhalla-Backend vorausliegend, kehrt als NuGet-Konsument nach 1.0 zurück
(siehe [DESIGN.md](DESIGN.md)).