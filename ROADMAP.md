# Heimdall — Roadmap zur 1.0

> Stand: 2026-08-22. Version 1.0.0 (SQLite-only, auf GitHub privat).
> Workstream A fertig (332 Tests grün, Commit 60a26f8). Workstream F (Metriken-
> Rollup, zwei-stufig raw+1m) umgesetzt (340 Tests grün, Commit 8e79c59).
> Workstream D (Public-Readiness: Apache-2.0, zentrale 1.0.0, Deterministic/
> SourceLink, CI Windows+Linux, Multi-Target-Tests net8/9/10, CHANGELOG/
> SECURITY) umgesetzt. Entscheidungen #3–#7 geklärt (siehe unten). Diese Roadmap
> ist ein lebendes Dokument — Stränge und Reihenfolge sind Vorschläge.

## 1.0-Ziel

Heimdall 1.0 ist die erste Version, die **öffentlich** auf nuget.org/github gehen
kann: self-contained (SQLite-only, keine Cross-Repo-Abhängigkeiten), operativ
konfigurierbar (Speicher & Retention pro Signal, Größenbegrenzung, Space-Reclaim),
pakettiert, gehärtet und CI-gesichert. Das Walhalla-Backend bleibt bewusst draußen
und kehrt als NuGet-Konsument nach 1.0 zurück.

---

## Workstream A — Storage & Retention konfigurierbar  *(fertig, Commit 60a26f8)*

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

## Workstream B — NuGet-Packaging & Distribution  *(umgesetzt — Packaging-Teil)*

Voraussetzung für den Walhalla-NuGet-Weg **und** für öffentliche 1.0-Pakete.
Entscheidung #5 (revidiert): **1.0 geht auf nuget.org UND als GitHub-Release** —
kein Code-Signing (nuget.org verlangt keines).

- `dotnet pack Heimdall.slnx -c Release -o artifacts/nupkg` packt alle 11
  Src-Projekte; Version zentral `1.0.0` (Directory.Build.props, siehe D2).
- Reproducible/Deterministic Builds (`ContinuousIntegrationBuild`,
  `Deterministic=true`, `SourceLink`, repo-URL) — umgesetzt in D3.
- NuGet-Metadaten je Paket: `Description`, `PackageTags`, zentrale `README.md`-
  Embed (`PackageReadmeFile` + Root-README ins nupkg), Lizenz-Expression
  (`Apache-2.0`, siehe #4), `Copyright`, `RepositoryUrl` + Commit (SourceLink).
- Verifiziert: 11 × `*.1.0.0.nupkg`, Nuspec mit License/Readme/Repo+Commit.
- Lokaler Feed (`artifacts/nupkg`) für Walhalla-Integrationstest / Pre-Release.
- **Offen (Release, Workstream E):** nuget.org-Push (API-Key) + GitHub-Release
  mit Paket-Artifacts; Reihenfolge/Trigger beim 1.0-Release festlegen.

## Workstream C — Operative Härtung

- **Rate-Limiting** auf den OTLP-Empfängern (HTTP + gRPC) — heute ungedrosselt
  (in `SESSION_STATUS` als offen notiert). Schutz gegen Last-Spitzen/fremde
 Exporter.
- Auth-Review: API-Key (OTLP/Prom) + Basic-Auth (UI) existieren minimal —
  Review auf 1.0-Anspruch (Timing-Safe-Vergleich, Header-Hygiene).
- **Self-Observability des Hosts (Entscheidung #6):** schlanke `heimdall.host.*`-
  Metriken **in 1.0** — minimaler Satz: Ingest-Counter pro Signal
  (`heimdall.host.ingest{signal=spans|logs|metrics}`) + Sweep-Latenz
  (`heimdall.host.sweep.duration`). Synthetisiert wie A4 (in-memory, nicht in
  heim_metrics gespeichert).
- Graceful Shutdown steht (Sink wird disposet); Flush der Ingest-Puffer beim
  Stopping-Signal verifizieren.

## Workstream D — Public-Readiness  *(umgesetzt)*

- `LICENSE` (Datei, **Apache-2.0** — Entscheidung #4) + `NOTICE` +
  `PackageLicenseExpression=Apache-2.0` zentral in `Directory.Build.props`.
- **Zentrale Versionierung 1.0.0** in `Directory.Build.props` (`Version`/
  `AssemblyVersion`/`FileVersion`); die per-csproj `0.1.0`-Sätze entfernt.
- **Deterministische Builds + SourceLink** (`Deterministic`,
  `ContinuousIntegrationBuild`, `PublishRepositoryUrl`, `EmbedUntrackedSources`,
  `Microsoft.SourceLink.GitHub` 10.0.400) — Zukunftssicherung (Repo bleibt bis
  1.0 privat).
- CI: `.github/workflows/build.yml` — `dotnet build` + `dotnet test` auf
  `net8/9/10` bei Push/PR, **Windows + ubuntu** (Entscheidung #7); SDKs 8/9/10.
- **Testprojekt multi-targetet** `net8/9/10`; Host-Boot-Tests via `#if NET10_0`
  auf net10 beschränkt, Mvc.Testing/Grpc/Host-Bezug bedingt (net8/9: ~302 Lib-
  Tests, net10: 340 inkl. Host-Boot-Tests).
- `CHANGELOG.md` (Keep-a-Changelog, [Unreleased] + [1.0.0]-Platzhalter).
- `SECURITY.md` (vertraulicher Meldeweg via GitHub Private Security Advisories
  / E-Mail).
- README-Politur: CI- + Lizenz- + nuget-Badges, Status-Zeile 1.0.0, Walhalla-
  Discoverability-Pointer auf DESIGN.md (Entscheidung #5 revidiert: nuget.org).
- README-`0.1.0`-Referenzen (Pack-Loop, Paket-Beispiele) auf `1.0.0` gehoben
  (Workstream B abgeschlossen).

## Workstream E — Release-Gates

- Alle Tests grün auf allen drei TFM (net8/9/10) unter CI.
- Smoke-Check im CI-Job (Host startet, `/otel` 200, OTLP-POST 200).
- Release-Checkliste: Versionskonsistenz, CHANGELOG-Eintrag, Tag `v1.0.0`,
  **nuget.org-Push** (alle 11 Pakete, API-Key) + GitHub-Release (Pakete als
  Artifacts), Repo `private → public`. Kein Code-Signing (#5).

---

## Workstream F — Metriken-Downsampling (Rollup)  *(umgesetzt, Entscheidung #3)*

Alte rohe Metrik-Punkte werden nicht nur hart gelöscht (A1/A2), sondern zuvor zu
niedriger Auflösung **aggregiert** — operativ nutzbare Langzeit-Trends bei
kleinem Footprint (1 Punkt/Min statt viele/Sek). **Umgesetzt als zwei-stufiges
Modell (raw + eine 1-Min-Stufe); Mehrstufig (1h/1d) post-1.0**, weil der Prom-
Lookback fix 5 Min ist (`SeriesResolver.DefaultLookbackMs=300_000`) und Stufen
>5 Min Lücken in Instant-Queries reißen würden. 1 Min ist lookback-sicher
(≥5 Rollup-Punkte im Lookback).

**Architektur:** gesamte Rollup-Logik liegt *inside* `Heimdall.Storage.SQLite`
(Sink + MetricSource-Partial) — kein Decorator, **null Vertragsänderung an
`Heimdall.Abstractions`**. Separate `heim_metrics_rollup`-Tabelle (Spiegel der
Wert-Spalten, `ts_unix_nano`→`bucket_start`+`resolution_seconds`, Index
`idx_heim_metrics_rollup_name_ts`). Additive Anlage via `CREATE IF NOT EXISTS`,
kein `user_version`-Bump.

- **Sweep-Übergang** (`RollupRawMetrics`, aufgerufen in `SweepRetention` *vor* der
  TTL-Löschung): Raw-Punkte mit `ts < boundary` (letzter voller Bucket ≤
  `now-RawDays`) in 5000er Batches lesen, in C# pro (Fingerprint, Bucket)
  aggregieren, ein Tx pro Batch: INSERT Rollup + DELETE Raw (idempotent — nach
  Commit sind die Raw-Zeilen weg). Aggregation pro Typ/Temporality: Gauge→LAST,
  Sum/Delta→SUM, Sum/Cumulative→LAST, Hist/Delta→elementweise SUM Buckets+sum+
  count, Hist/Cumulative→LAST; MIN/MAX je Range. `attrs_json`/`resource_json`
  verbatim (Labels/Matcher arbeiten identisch).
- **Disjointness konstruktiv:** eine Rollup-Zeile entsteht nur, wenn ihre Raw-
  Zeilen im selben Tx gelöscht wurden → `heim_metrics` und `heim_metrics_rollup`
  sind disjunkt; `FetchRealPoints` UNION ALL beider Tabellen **ohne Boundary-
  Filter** zählt kein logisches Sample doppelt.
- **Config:** `HeimdallRollupOptions { Enabled=false, ResolutionSeconds=60,
  RawDays=1 }` auf `SQLiteTelemetryOptions` UND host-seitig `HeimdallStorageOptions`.
  **Opt-In (Default off)** — bestehende Deployments unverändert. Validierung:
  `ResolutionSeconds<=0`, `RawDays<0`, `RawDays>MetricsDaysEffective` (bei Enabled)
  → Startup-Fehler.
- **Cap-Eviction (A2):** Rollup-Zeilen zählen fürs `MaxBytes`-Cap und sind evictbar
  (`OldestRows`-UNION, `SourceTable`-Mapping); Eviction-Counter in
  `heimdall.retention.evicted{signal=metrics}` gefaltet (keine Kardinalitäts-/
  Dashboard-Breakage). Rollup-TTL über `bucket_start` mit `MetricsDaysEffective`,
  in `heimdall.retention.deleted{signal=metrics}` gefaltet.
- **Query-Merge:** `FetchRealPoints` UNION ALL raw+rollup (wenn enabled),
  `ListMetricNames`/`ScanLabelRows` UNION beider Tabellen — sonst verschwindet ein
  Name/Label, sobald seine Raw-Punkte alle gealtert sind. Disabled = heute
  (regression-sicher).
- **Tests:** `RollupTests.cs` (8): Validierung, Aggregation pro Typ, Disjointness,
  Query-Parität Raw/Roll, Discovery-Parität nach Alterung, Sweep-Idempotenz,
  Cap-Eviction über Rollup, Disabled==heute. 340 Tests grün.

### 1.0-Limitationen

- Opt-In (`Enabled=false` Default); **eine Stufe (1 Min)**; Mehrstufig post-1.0.
- `*_over_time` über Roll-Fenster grob (1 Pt/Min); `count_over_time` liefert
  ~Minuten, nicht den originalen Sample-Count.
- Sub-Resolution `rate()`/`irate()` (`[<1m]`) über alte Daten jenseits `RawDays`
  → NaN.
- Cumulative-Sum/Gauge-Rollup-Punkte sind „as-of bucket_start"-Snapshots; Inter-
  Punkt-Zeitdelta ~Resolution, nicht echtes Inter-Sample-Delta.
- Attribut-Reihenfolge-Variation kann eine logische Serie in mehrere Rollup-Serien
  spalten (selbstkorrigierend, selten — selbes Verhalten wie bei Raw-Queries).
- Cap kann die effektive Rollup-Retention unter `MetricsDaysEffective` drücken
  (Cap ist harte Schranke).
- **Erstlauf auf großer Alt-DB:** Roll aktiviert auf DB mit Monaten Raw → erster
  Sweep rollt riesigen Bereich in 5000er Batches (kurze Tx je Batch, `try/catch`
  im Sweep, Crash nicht fatal). Erst-Aktivierung kann mehrere Sweep-Zyklen
  brauchen; Queries sehen bis dahin Raw (korrekt).

---

## Bewusst nicht in 1.0

- **Walhalla-Backend** — kehrt als NuGet-Konsument nach 1.0 zurück (sobald
  `Heimdall.Abstractions` gepackt ist; siehe README-Abschnitt „Walhalla-Backend").
- **Per-Attribute-Retention** (z. B. `service.name=x` länger behalten) — post-1.0.
- **Exemplars** (Metrics↔Traces punktscharf) — seit 0.1 offen, post-1.0.
- **Multi-Tenancy / mehrere Speicherinstanzen** — nicht 1.0.

---

## Offene Entscheidungen (vor Umsetzung klären)

1. ~~**Cap-Granularität (A2):**~~ ✅ Gesamt-Cap `MaxBytes`.
2. ~~**Space-Reclaim (A3):**~~ ✅ `auto_vacuum=INCREMENTAL` + VACUUM-Migration.
3. ~~**Metriken-Rollup:**~~ ✅ **Downsampling IN 1.0** (Workstream F) — keine
   harte Löschung, sondern Rollup alter Metrik-Punkte.
4. ~~**Lizenz (D):**~~ ✅ **Apache-2.0** (LICENSE-Datei + `License-Expression`).
5. ~~**NuGet-Signing/Publish (B):**~~ ✅ **nuget.org + GitHub-Release bei 1.0** —
   kein Code-Signing (nuget.org verlangt keines). (Revidiert: ursprünglich nur
   GitHub-Release; nuget.org-Push jetzt Teil von 1.0.)
6. ~~**Self-Observability des Hosts (C):**~~ ✅ **minimales `heimdall.host.*`-Set
   in 1.0** (Ingest-Counter pro Signal + Sweep-Latenz).
7. ~~**CI-Betriebssysteme (D):**~~ ✅ **Windows + ubuntu** (build+test net8/9/10).