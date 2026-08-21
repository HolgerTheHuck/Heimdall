# Heimdall — Roadmap zur 1.0

> Stand: 2026-08-21. 0.1.0 gebaut (313 Tests grün, SQLite-only, auf GitHub privat).
> Diese Roadmap ist ein lebendes Dokument — Stränge und Reihenfolge sind Vorschläge.

## 1.0-Ziel

Heimdall 1.0 ist die erste Version, die **öffentlich** auf nuget.org/github gehen
kann: self-contained (SQLite-only, keine Cross-Repo-Abhängigkeiten), operativ
konfigurierbar (Speicher & Retention pro Signal, Größenbegrenzung, Space-Reclaim),
pakettiert, gehärtet und CI-gesichert. Das Walhalla-Backend bleibt bewusst draußen
und kehrt als NuGet-Konsument nach 1.0 zurück.

---

## Workstream A — Storage & Retention konfigurierbar  *(aktiver Strang)*

Heute: ein globales `RetentionDays` (0 = unbegrenzt) für spans/logs/metrics
gemeinsam; kein Größenlimit; keine Platzrückgewinnung. Indizes auf den Zeit-Spalten
existieren bereits (`SQLiteTelemetrySink.cs:418-421`), der Sweep ist indexgestützt.

### A1 — Per-Signal-Retention (TTL pro Signal)

Statt einer globalen Frist je Signal ein eigener Wert.

Config-Skizze (`HeimdallStorageOptions` / `SQLiteTelemetryOptions`):

```jsonc
"Storage": {
  "Backend": "sqlite",
  "DataPath": "var/heimdall/otel.db",
  "Retention": {
    "TracesDays": 3,     // 0 = unbegrenzt
    "LogsDays": 14,
    "MetricsDays": 30
  },
  "RetentionSweepMinutes": 30
}
```

- `RetentionDays` (alt) bleibt als **Abwärtskompat-Fallback**: falls `Retention.*`
  nicht gesetzt, gilt `RetentionDays` für alle drei Signale (bestehende
  appsettings unverändert lauffähig).
- Sweep (`SQLiteTelemetrySink.SweepRetention`) rechnet pro Tabelle einen eigenen
  Cutoff: `heim_spans`→`TracesDays`, `heim_logs`→`LogsDays`,
  `heim_metrics`→`MetricsDays`. Typischer Fall „Logs länger behalten als Traces"
  wird konfigurierbar.
- Validierung: negative Werte → Startup-Fehler; `SweepMinutes` ≥ 1.

### A2 — Größenbasierte Begrenzung (Storage-Cap) — *Gesamt-Cap*

Entschieden: ein harter Plafond `MaxBytes` über die gesamte `otel.db`, damit die
DB nie den Host vollschreibt. Per-Signal-Caps bewusst später.

```jsonc
"Storage": {
  "Retention": { "TracesDays": 3, "LogsDays": 14, "MetricsDays": 30 },
  "MaxBytes": 1073741824,   // 1 GiB; 0 = unbegrenzt
  "RetentionSweepMinutes": 30
}
```

Eviction-Strategie: nach dem zeitbasierten Sweep prüfen, ob die DB-Datei
(`PRAGMA page_count * page_size`) `MaxBytes` überschreitet. Wenn ja, älteste Zeilen
signalübergreifend (kleinstes `min(start_unix_nano / ts_unix_nano)` zuerst,
indexgestützt) in Tranchen löschen, bis ein Ziel-Füllgrad (z. B. 90 %) erreicht ist.
Danach `incremental_vacuum` (siehe A3). `MaxBytes=0` = unbegrenzt (Default).

### A3 — Space-Reclaim (Datei schrumpft nach DELETE) — *auto_vacuum + Migration*

Entschieden: `auto_vacuum = INCREMENTAL`. SQLite gibt gelöschte Seiten intern frei,
**verkleinert die Datei aber nicht** ohne `VACUUM`/`auto_vacuum`.

- Beim Schema-Bootstrap `PRAGMA auto_vacuum = INCREMENTAL` setzen (wirkt nur auf
  frische DBs). **Achtung:** `auto_vacuum` muss stehen, *bevor* die Tabellen
  angelegt werden — im Bootstrap-Pfad also als erstes Statement.
- Nach jedem Sweep/Eviction-Schritt `PRAGMA incremental_vacuum(N)` (N Seiten),
  gebunden an den Lock-Gate wie der Sweep.
- **Migration bestehender DBs:** `auto_vacuum` lässt sich nachträglich nicht
  umschalten; einmaliger `VACUUM` beim Start, wenn die DB noch im Legacy-Modus
  ist (erkennbar an `PRAGMA auto_vacuum` == 0) und ein Migrations-Flag/Version
  das anzeigt. Nur einmal, dann nie wieder (VACUUM ist teuer/exklusiv).

### A4 — Retention- & Eviction-Observability

Damit Betreiber sehen, was gelöscht wurde: der Sink führt Zähler und stellt sie
über die bestehende `IHeimdallMetricSource`-Seite als `heimdall.retention.*`
bereit (Counters/Gauges):

- `heimdall.retention.deleted{signal=spans|logs|metrics}` — gelöschte Zeilen pro Sweep.
- `heimdall.retention.evicted{signal=...}` — größengetrieben gelöscht.
- `heimdall.storage.bytes` — aktuelle DB-Dateigröße (Gauge).
- `heimdall.storage.rows{signal=...}` — aktuelle Zeilenzahl (Gauge).

Ggf. Admin-Fläche im Dashboard („Storage"-Karte: Größe, Zeilenzahlen, nächste
Sweep-Zeit). Rein additiv, kein Vertragsbruch.

### A5 — Tests

- Per-Signal-TTL: pro Signal unterschiedlich alte Zeilen → nur die über ihrer
  Frist werden gelöscht, die anderen bleiben.
- Legacy-Fallback: nur `RetentionDays` gesetzt → alle Signale wie heute.
- Cap-Eviction: DB über `MaxBytes` treiben → älteste fallen, Dateigröße sinkt
  nach `incremental_vacuum` messbar.
- Reclaim: vor/nach-Vergleich der Dateigröße nach Sweep.
- Validierung: negative Fristen → Startup-Exception.

---

## Workstream B — NuGet-Packaging & Distribution

Voraussetzung für den Walhalla-NuGet-Weg **und** für öffentliche 1.0-Pakete.

- `dotnet pack` für alle Src-Projekte (Loop steht im README). Version `0.1.0 → 1.0.0`.
- Reproducible/Deterministic Builds (`ContinuousIntegrationBuild`-Flag,
  `Deterministic=true`, `SourceLink`, repo-URL in `Directory.Build.props`).
- NuGet-Metadaten je Paket: Beschreibung, `README.md`-Embed, Lizenz-Expression,
  Tags, `RepositoryUrl`, Source-Link.
- Lokaler Feed (`artifacts/nupkg`) für Walhalla-Integrationstest, bevor 1.0
  öffentlich geht.
- Öffnen: paketsignierung (signieren?) und nuget.org-Publish-Flow.

## Workstream C — Operative Härtung

- **Rate-Limiting** auf den OTLP-Empfängern (HTTP + gRPC) — heute ungedrosselt
  (in `SESSION_STATUS` als offen notiert). Schutz gegen Last-Spitzen/fremde
 Exporter.
- Auth-Review: API-Key (OTLP/Prom) + Basic-Auth (UI) existieren minimal —
  Review auf 1.0-Anspruch (Timing-Safe-Vergleich, Header-Hygiene).
- Self-Observability des SQLite-Hosts: da die Walhalla-Selbst-Obs entfallen ist,
  optional eine schlanke `heimdall.host.*`-Metrik (Ingest-Raten, Buffer-Tiefstand,
  Sweep-Latenz) — nur falls 1.0 das will.
- Graceful Shutdown steht (Sink wird disposet); Flush der Ingest-Puffer beim
  Stopping-Signal verifizieren.

## Workstream D — Public-Readiness

- `LICENSE` (Datei) + Lizenz-Expression in den csproj — **offen: welche Lizenz?**
- CI: `.github/workflows/build.yml` — `dotnet build` + `dotnet test` auf
  `net8/9/10` bei Push/PR (Windows + ubuntu).
- `CHANGELOG.md` (Keep-a-Changelog-Stil), ab 1.0.0 gepflegt.
- `SECURITY.md` (Meldeweg) und ggf. `CONTRIBUTING.md`.
- README-Politur für Public: Quickstart, Badges (CI-Status, Lizenz, nuget), da
  die Walhalla-Historie für Außenstehende aus dem DESIGN.md-Banner ersichtlich ist.
- Versionsbumpte aller Pakete auf `1.0.0`.

## Workstream E — Release-Gates

- Alle Tests grün auf allen drei TFM (net8/9/10) unter CI.
- Smoke-Check im CI-Job (Host startet, `/otel` 200, OTLP-POST 200).
- Release-Checkliste: Versionskonsistenz, CHANGELOG-Eintrag, Tag `v1.0.0`,
  GitHub-Release, nuget.org-Push (falls gewünscht), Repo `private → public`.

---

## Bewusst nicht in 1.0

- **Walhalla-Backend** — kehrt als NuGet-Konsument nach 1.0 zurück (sobald
  `Heimdall.Abstractions` gepackt ist; siehe README-Abschnitt „Walhalla-Backend").
- **Metriken-Downsampling/Rollup** — alte rohe Metrik-Punkte zu niedriger Auflösung
  aggregieren, statt sie zu löschen. Größerer Eingriff in `IHeimdallMetricSource`;
  post-1.0.
- **Per-Attribute-Retention** (z. B. `service.name=x` länger behalten) — post-1.0.
- **Exemplars** (Metrics↔Traces punktscharf) — seit 0.1 offen, post-1.0.
- **Multi-Tenancy / mehrere Speicherinstanzen** — nicht 1.0.

---

## Offene Entscheidungen (vor Umsetzung klären)

1. ~~**Cap-Granularität (A2):**~~ ✅ Gesamt-Cap `MaxBytes`.
2. ~~**Space-Reclaim (A3):**~~ ✅ `auto_vacuum=INCREMENTAL` + VACUUM-Migration.
3. **Metriken-Rollup:** bestätigen, dass Downsampling post-1.0 bleibt (kein
   Rollup, sondern harte Löschung alter Metrik-Punkte in 1.0)?
4. **Lizenz (D):** MIT, Apache-2.0 oder eine restriktivere? (Für Walhalla-Kompat
   und Public-Ziel relevant.)
5. **NuGet-Signing/Publish (B):** paketsigniert und auf nuget.org bei 1.0, oder
   nur GitHub-Release?
6. **Self-Observability des Hosts (C):** schlanke `heimdall.host.*`-Metriken in
   1.0 aufnehmen oder aufschieben?
7. **CI-Betriebssysteme (D):** Windows-only (aktuelle Dev-Umgebung) oder
   zusätzlich ubuntu?