using System;

namespace Heimdall.Storage.SQLite;

/// <summary>
/// Konfiguration des SQLite-gestuetzten Heimdall-Storage.
/// </summary>
public sealed class SQLiteTelemetryOptions
{
    /// <summary>
    /// Dateipfad der SQLite-DB ("otel.db") oder ":memory:" fuer einen
    /// fluechtigen Bestand. Default: "heimdall-otel.db".
    /// </summary>
    public string DataPath { get; set; } = "heimdall-otel.db";

    /// <summary>
    /// Retention in Tagen; 0 = unbegrenzt. Default 7. Dient als **Abwaertskompat-
    /// Fallback**: pro Signal gilt <see cref="HeimdallRetentionOptions"/> (falls
    /// gesetzt), sonst dieser Wert. Bestehende appsettings (nur RetentionDays)
    /// bleiben unverändert lauffähig.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// Per-Signal-Retention (TTL pro Signal). null-Werte fallen auf
    /// <see cref="RetentionDays"/> zurück; 0 = explizit unbegrenzt.
    /// </summary>
    public HeimdallRetentionOptions Retention { get; set; } = new();

    /// <summary>
    /// Harter Plafond über die gesamte DB-Datei in Bytes; 0 = unbegrenzt.
    /// Bei Ueberschreitung evictet der Sweeper aelteste Zeilen signaluebergreifend
    /// bis zum Ziel-Fuellgrad (90 %). Default 0.
    /// </summary>
    public long MaxBytes { get; set; }

    /// <summary>Intervall des Retention-Sweepers (Minuten). 0 = Sweep deaktiviert. Default 30.</summary>
    public int RetentionSweepMinutes { get; set; } = 30;

    /// <summary>SQLite-Verbindung zusatzlich geoeffnet mit foreign_keys=ON und WAL.</summary>
    public bool WalMode { get; set; } = true;

    /// <summary>
    /// auto_vacuum=INCREMENTAL beim Bootstrap setzen und nach jedem Sweep
    /// <c>PRAGMA incremental_vacuum</c> laufen lassen, damit die DB-Datei nach
    /// DELETE/Eviction schrumpft. Default true. Nur wirksam bei frischen DBs
    /// bzw. nach der <see cref="VacuumMigrateLegacy"/>-Migration.
    /// </summary>
    public bool AutoVacuum { get; set; } = true;

    /// <summary>
    /// Einmaliger <c>VACUUM</c> beim Start, falls eine Legacy-DB (auto_vacuum=0,
    /// user_version=0) vorliegt — reorganisiert die DB mit dem neuen auto_vacuum.
    /// Teuer/exklusiv, darum self-gating via user_version (nur einmal). false =
    /// Notaus fuer große Alt-DBs (dann kein Space-Reclaim bis zur manuellen
    /// Migration). Default true.
    /// </summary>
    public bool VacuumMigrateLegacy { get; set; } = true;

    /// <summary>
    /// Metriken-Downsampling (Rollup): rohe Metrik-Punkte werden nach
    /// <see cref="HeimdallRollupOptions.RawDays"/> Tagen zu
    /// <see cref="HeimdallRollupOptions.ResolutionSeconds"/>-Buckets aggregiert
    /// (statt hart gelöscht) und bis <see cref="MetricsDaysEffective"/> gehalten.
    /// **Opt-In** (Default off) — bestehende Deployments unverändert. Siehe
    /// Workstream F (ROADMAP).
    /// </summary>
    public HeimdallRollupOptions Rollup { get; set; } = new();

    // -----------------------------------------------------------------------
    // Effective-Werte (Fallback-Logik zentral hier, nicht im Sink/Host).
    // int? ?? int: null -> RetentionDays; 0 bleibt 0 (explizit unbegrenzt).
    // -----------------------------------------------------------------------

    /// <summary>Effektive Trace-Retention in Tagen (0 = unbegrenzt).</summary>
    public int TracesDaysEffective => Retention?.TracesDays ?? RetentionDays;
    /// <summary>Effektive Log-Retention in Tagen (0 = unbegrenzt).</summary>
    public int LogsDaysEffective => Retention?.LogsDays ?? RetentionDays;
    /// <summary>Effektive Metric-Retention in Tagen (0 = unbegrenzt).</summary>
    public int MetricsDaysEffective => Retention?.MetricsDays ?? RetentionDays;

    /// <summary>True, wenn mindestens ein Signal eine zeitliche Frist hat.</summary>
    public bool AnyTimeRetention =>
        TracesDaysEffective > 0 || LogsDaysEffective > 0 || MetricsDaysEffective > 0;

    /// <summary>True, wenn der Sweeper etwas zu tun hat (Frist ODER Cap).</summary>
    public bool SweepActive => (AnyTimeRetention || MaxBytes > 0) && RetentionSweepMinutes > 0;

    // --- Rollup-Effective (Fallback auf Defaults, wenn Rollup null) --------
    /// <summary>True, wenn Rollup aktiv ist (Fallback false).</summary>
    public bool RollupEnabledEffective => Rollup?.Enabled ?? false;
    /// <summary>Effektive Rollup-Auflösung in Sekunden (Fallback 60).</summary>
    public int RollupResolutionSecondsEffective => Rollup?.ResolutionSeconds ?? 60;
    /// <summary>Effektive Raw-Haltedauer in Tagen, bevor gerollt wird (Fallback 1).</summary>
    public int RollupRawDaysEffective => Rollup?.RawDays ?? 1;

    /// <summary>
    /// Wirft bei ungueltigen Werten. Authoritativ (deckt Embedded-Nutzung ohne
    /// Host-Validierung). Negativ-Fristen, MaxBytes&lt;0, SweepMinutes&lt;0,
    /// Rollup-Auflösung/Raw-Tage, Raw&gt;MetricsDays.
    /// </summary>
    public void Validate()
    {
        if (RetentionDays < 0)
            throw new InvalidOperationException(
                $"Heimdall:Storage:RetentionDays „{RetentionDays}“ ungültig — negativ nicht erlaubt (0 = unbegrenzt).");
        if (Retention is not null)
        {
            if (Retention.TracesDays < 0 || Retention.LogsDays < 0 || Retention.MetricsDays < 0)
                throw new InvalidOperationException(
                    "Heimdall:Storage:Retention:{Traces,Logs,Metrics}Days ungültig — negativ nicht erlaubt (0 = unbegrenzt, null = Fallback).");
        }
        if (MaxBytes < 0)
            throw new InvalidOperationException(
                $"Heimdall:Storage:MaxBytes „{MaxBytes}“ ungültig — negativ nicht erlaubt (0 = unbegrenzt).");
        if (RetentionSweepMinutes < 0)
            throw new InvalidOperationException(
                $"Heimdall:Storage:RetentionSweepMinutes „{RetentionSweepMinutes}“ ungültig — negativ nicht erlaubt (0 = Sweep deaktiviert).");
        if (Rollup is not null)
        {
            if (Rollup.ResolutionSeconds <= 0)
                throw new InvalidOperationException(
                    $"Heimdall:Storage:Rollup:ResolutionSeconds „{Rollup.ResolutionSeconds}“ ungültig — muss > 0 sein.");
            if (Rollup.RawDays < 0)
                throw new InvalidOperationException(
                    $"Heimdall:Storage:Rollup:RawDays „{Rollup.RawDays}“ ungültig — negativ nicht erlaubt (0 = sofort rollen).");
            if (Rollup.Enabled && MetricsDaysEffective > 0 && Rollup.RawDays > MetricsDaysEffective)
                throw new InvalidOperationException(
                    $"Heimdall:Storage:Rollup:RawDays „{Rollup.RawDays}“ > MetricsDaysEffective „{MetricsDaysEffective}“ — Rollup-Fenster wäre leer (Raw wird vor dem Rollen gelöscht).");
        }
    }
}

/// <summary>
/// Per-Signal-Retention (TTL pro Signal). null = nicht gesetzt (Fallback auf
/// <see cref="SQLiteTelemetryOptions.RetentionDays"/>); 0 = explizit unbegrenzt.
/// </summary>
public sealed class HeimdallRetentionOptions
{
    /// <summary>Trace-Retention in Tagen (null = Fallback, 0 = unbegrenzt).</summary>
    public int? TracesDays { get; set; }
    /// <summary>Log-Retention in Tagen (null = Fallback, 0 = unbegrenzt).</summary>
    public int? LogsDays { get; set; }
    /// <summary>Metric-Retention in Tagen (null = Fallback, 0 = unbegrenzt).</summary>
    public int? MetricsDays { get; set; }
}

/// <summary>
/// Metriken-Downsampling (Rollup) — rohe Metrik-Punkte werden nach
/// <see cref="RawDays"/> Tagen zu <see cref="ResolutionSeconds"/>-Buckets
/// aggregiert (statt hart gelöscht) und bis zur gesamten Metric-Retention
/// (<c>MetricsDaysEffective</c>) gehalten. **Opt-In** (Default off). Siehe
/// Workstream F (ROADMAP). 1.0: eine Stufe (1 Min); Mehrstufig post-1.0.
/// </summary>
public sealed class HeimdallRollupOptions
{
    /// <summary>Rollup aktivieren. Default false (bestehende Deployments unverändert).</summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// Bucket-Auflösung in Sekunden. Default 60 (1 Min — lookback-sicher für den
    /// fixen 5-Min-Prom-Lookback). Stufen &gt; 300 würden Instant-Queries lücken.
    /// </summary>
    public int ResolutionSeconds { get; set; } = 60;
    /// <summary>
    /// Tage, die rohe Metrik-Punkte unangetastet bleiben, bevor sie zu Buckets
    /// aggregiert werden. Default 1. 0 = sofort rollen. Muss ≤ MetricsDaysEffective
    /// sein (sonst ist das Rollup-Fenster leer — siehe Validate).
    /// </summary>
    public int RawDays { get; set; } = 1;
}