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

    /// <summary>Retention in Tagen; 0 = unbegrenzt. Default 7.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Intervall des Retention-Sweepers (Minuten). Default 30.</summary>
    public int RetentionSweepMinutes { get; set; } = 30;

    /// <summary>SQLite-Verbindung zusatzlich geoeffnet mit foreign_keys=ON und WAL.</summary>
    public bool WalMode { get; set; } = true;
}