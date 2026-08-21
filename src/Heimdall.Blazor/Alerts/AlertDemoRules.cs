using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Wiederverwendbare Demo-Alarmregeln (Metrik 5xx-Rate + Log-Fehler-Haeufung).
// Host und Samples rufen Seed() auf — idempotent (ueberspringt Namen, die schon
// existieren). Beide Regeln auf den Logger-Kanal (Demo-Alerts im Konsol-Log).
// ---------------------------------------------------------------------------

/// <summary>Seedet zwei Beispiel-Alarmregeln, falls noch nicht vorhanden.</summary>
public static class AlertDemoRules
{
    /// <summary>Legt die Demo-Regeln an, deren Name noch nicht im Store existiert.</summary>
    public static void Seed(IAlertRuleStore store)
    {
        if (store is null) return;
        var existing = store.List().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        SaveIfNew(store, existing, FiveXxErrorRate());
        SaveIfNew(store, existing, ErrorLogsSurge());
    }

    /// <summary>Metrik-Regel: 5xx-Antwortrate der letzten 5 min &gt; 0 (for 30 s).</summary>
    public static AlertRule FiveXxErrorRate() => new(
        Id: "", Name: "5xx-Fehlerrate",
        Enabled: true, Signal: AlertSignal.Metric,
        Promql: "sum(rate(http_requests_total{status=~\"5..\"}[5m])) > 0",
        LogText: null, MinSeverity: null,
        HasError: null, ServiceName: null, NameContains: null,
        WindowSeconds: 300, Threshold: 0, ForSeconds: 30,
        Channels: new[] { "logger" },
        Description: "Feuert, wenn im 5-Minuten-Fenster 5xx-Antworten anfallen (RED-Metrik aus Server-Spans).",
        EvalIntervalSeconds: 0);

    /// <summary>Log-Regel: &gt; 5 ERROR-Logs (Severity&gt;=17) im 5-Minuten-Fenster.</summary>
    public static AlertRule ErrorLogsSurge() => new(
        Id: "", Name: "Fehler-Logs gehäuft",
        Enabled: true, Signal: AlertSignal.Log,
        Promql: null, LogText: null, MinSeverity: 17,
        HasError: null, ServiceName: null, NameContains: null,
        WindowSeconds: 300, Threshold: 5, ForSeconds: 0,
        Channels: new[] { "logger" },
        Description: "Feuert bei mehr als 5 ERROR-Logs (Severity>=17) im 5-Minuten-Fenster (FTS5/tsvector).",
        EvalIntervalSeconds: 0);

    private static void SaveIfNew(IAlertRuleStore store, HashSet<string> existing, AlertRule rule)
    {
        if (existing.Contains(rule.Name)) return;
        try { store.Save(rule); } catch { /* idempotent best-effort */ }
    }
}