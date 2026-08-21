using System.Collections.Generic;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Alarm-Modell. Eine Regel beschreibt EINE Bedingung ueber einen der drei
// Signale (Metrik/Log/Trace), ein Fenster, einen Schwellen und eine `for`-
// Dauer (Pending→Firing). Der Zustandsautomat (AlertEvent) wird vom
// AlertEvaluator getaktet und im AlertStateStore persistiert.
//
// JSON via JsonSerializer (POCO-Schema, WriteIndented) — nicht das hand-
// gerollte JsonDocument des Grafana-Stores, da wir hier ein eigenes Schema
// serialisieren und Records nativ unterstuetzt werden.
// ---------------------------------------------------------------------------

/// <summary>Signal-Typ einer Alarmregel.</summary>
public enum AlertSignal
{
    /// <summary>PromQL-Ausdruck; feuert = nicht-leerer Vektor (Vergleich behaelt Treffer).</summary>
    Metric,
    /// <summary>Volltext-Logsuche (FTS5/tsvector auf body) + MinSeverity; feuert = Trefferzahl &gt; Schwellen.</summary>
    Log,
    /// <summary>Trace-Filter (HasError/Service/Name); feuert = Trefferzahl &gt; Schwellen.</summary>
    Trace
}

/// <summary>Zustand einer Regel im Zustandsautomat.</summary>
public enum AlertState
{
    /// <summary>Bedingung nicht erfuellt (ruhig).</summary>
    Ok,
    /// <summary>Bedingung erfuellt, aber `for`-Dauer noch nicht abgelaufen.</summary>
    Pending,
    /// <summary>Bedingung erfuellt und `for`-Dauer abgelaufen — aktiv alarmiert.</summary>
    Firing,
    /// <summary>Bedingung war erfuellt und ist gerade wieder weggefallen — aufgeloest.</summary>
    Resolved
}

/// <summary>
/// Eine Alarmregel. Signal-spezifische Felder sind nur fuer den jeweiligen
/// <see cref="Signal"/> relevant (Metric→<see cref="Promql"/>; Log→
/// <see cref="LogText"/>/<see cref="MinSeverity"/>; Trace→<see cref="HasError"/>/
/// <see cref="ServiceName"/>/<see cref="NameContains"/>).
/// </summary>
public sealed record AlertRule(
    string Id,
    string Name,
    bool Enabled,
    AlertSignal Signal,
    string? Promql,                  // Metric: kompletter PromQL inkl. Vergleich (rate(...)[5m]) > 0.1)
    string? LogText,                 // Log: FTS5/tsvector-Query auf body
    int? MinSeverity,                // Log: Mindest-Severity (OTel-Int: Error=17, Warn=13, Info=9)
    bool? HasError,                  // Trace: nur Fehler-Traces
    string? ServiceName,             // Trace: Service-Filter
    string? NameContains,            // Trace: Span-Namen-Filter
    long WindowSeconds,              // Log/Trace: Fenster in Sekunden
    int Threshold,                   // Log/Trace: feuert bei Trefferzahl > Threshold
    long ForSeconds,                 // Pending-Dauer bis Firing (Grafana `for`)
    IReadOnlyList<string> Channels,  // ["email","webhook"] — nach Namen aufgeloest
    string? Description,
    long EvalIntervalSeconds)        // 0 = globaler Takt (EvaluationIntervalSeconds)
{
    /// <summary>Leere Channels-Liste als Default (verhindert null bei alten Regeln).</summary>
    public static IReadOnlyList<string> EmptyChannels { get; } = new List<string>();
}

/// <summary>Listen-Eintrag (Regel-Ref ohne volle Definition).</summary>
public sealed record AlertRuleRef(string Id, string Name, AlertSignal Signal, bool Enabled);

/// <summary>
/// Zustand + Diagnose einer Regel (pro RuleId eine Instanz im StateStore).
/// <see cref="SinceUnixMs"/> = seit wann der aktuelle Zustand gilt.
/// </summary>
public sealed record AlertEvent(
    string RuleId,
    AlertState State,
    long SinceUnixMs,
    long? LastNotifiedUnixMs,
    double? LastValue,
    string? Message,
    long LastEvalUnixMs);

/// <summary>
/// Benachrichtigung, die an einen <see cref="Channels.IAlertChannel"/> gesendet
/// wird. <see cref="State"/> ist Firing oder Resolved (fuer die Transition-
/// Benachrichtigung).
/// </summary>
public sealed record AlertNotification(
    string RuleName,
    AlertSignal Signal,
    AlertState State,
    double Value,
    string? Message,
    long FiredAtUnixMs,
    string RuleId,
    string BasePath);