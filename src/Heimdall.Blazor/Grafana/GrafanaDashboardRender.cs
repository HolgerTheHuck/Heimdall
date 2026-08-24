using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.Blazor.Grafana;

// ---------------------------------------------------------------------------
// Geteilte, werferfreie Vorbereitung der Dashboard-Auswertung — extrahiert
// aus der früher inline in <c>GrafanaDashboardViewPage.OnInitialized</c>
// stehenden Logik, damit Shell (Platzhalter) und der Per-Panel-Endpoint
// identisch rechnen: derselbe Zeitbereich, dasselbe Step, dieselben Render-
// Variablen, dieselbe Slot-Reihenfolge. Der Index in <see cref="ExpandPanels"/>
// ist der stabile Schlüssel für den Panel-Endpoint (nicht <c>GrafanaPanel.Id</c>,
// das bei Repeat-Expansion nicht eindeutig ist).
//
// Bewusst ohne <c>ResolveOptions</c>: die Dropdown-Optionen sind teuer
// (ScanLabelRows) und nur für das Filter-Form der Shell nötig — nicht für die
// Panel-Auswertung. Sie bleiben allein in der Page.
// ---------------------------------------------------------------------------

/// <summary>Statischer Helfer für die Dashboard-Render-Vorbereitung.</summary>
public static class GrafanaDashboardRender
{
    /// <summary>Zeitbereich (ms + ns) + Step + Render-Variablen für ein Dashboard.</summary>
    public sealed record RenderPrep(
        long FromMs, long ToMs, long StepMs,
        long FromNs, long ToNs,
        IReadOnlyDictionary<string, string> RenderVars);

    /// <summary>
    /// Löst Zeitbereich, Step und die Render-Variablen (Template-Variablen +
    /// Grafana-Built-ins) für ein Dashboard auf. Rein, wirft nie. Der Index
    /// der Rückgabe-<see cref="ExpandPanels"/> baut auf diesen Werten auf.
    /// </summary>
    /// <param name="dash">Das geladene Dashboard.</param>
    /// <param name="vars">Die <c>var-*</c> Query-Parameter (oder null).</param>
    /// <param name="preset">Preset-Key (z. B. <c>"1h"</c>) oder null.</param>
    /// <param name="fromNs">Explizite <c>from</c>-Schranke in Unix-ns oder null.</param>
    /// <param name="toNs">Explizite <c>to</c>-Schranke in Unix-ns oder null.</param>
    /// <param name="nowUnixNano">Aktueller Zeitpunkt in Unix-ns (durchgereicht für Tests).</param>
    /// <param name="fallbackPreset">Preset, falls weder Preset noch from/to vorliegen.</param>
    public static RenderPrep BuildRenderVars(
        GrafanaDashboard dash,
        IReadOnlyDictionary<string, string>? vars,
        string? preset, long? fromNs, long? toNs,
        long nowUnixNano, string fallbackPreset = "1h")
    {
        var range = HeimdallRange.Resolve(preset, fromNs, toNs, nowUnixNano, fallbackPreset);
        long fromNsRes = range.From ?? 0L;
        long toNsRes = range.To ?? nowUnixNano;
        long fromMs = fromNsRes / 1_000_000L;
        long toMs = toNsRes / 1_000_000L;
        long stepMs = Math.Max(1_000L, (toMs - fromMs) / 120L);
        if (stepMs < 1_000L) stepMs = 1_000L;

        var renderVars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in dash.Templating)
        {
            if (string.IsNullOrEmpty(v.Name)) continue;
            if (string.Equals(v.Type, "datasource", StringComparison.OrdinalIgnoreCase)) continue;
            renderVars[v.Name] = GrafanaTemplating.SelectedValue(v, vars);
        }

        // Grafana-Built-in-Variablen ($__interval/$__rate_interval/$__range) aus
        // Step und Zeitraum ableiten — siehe GrafanaTemplating.BuiltIns.
        foreach (var kv in GrafanaTemplating.BuiltIns(fromMs, toMs, stepMs))
            renderVars[kv.Key] = kv.Value;

        return new RenderPrep(fromMs, toMs, stepMs, fromNsRes, toNsRes, renderVars);
    }

    /// <summary>
    /// Expandiert die Panels eines Dashboards in Render-Slots: Panels in
    /// Grafana-Lesenreihenfolge (Y, dann X), Panels mit <c>repeat</c>-Variable
    /// pro gewähltem Wert zu einem eigenen Slot (Title interpoliert). Der Index
    /// in der Rückgabe ist der stabile Schlüssel für den Per-Panel-Endpoint.
    /// </summary>
    /// <returns>Ein Slot trägt das Original-Panel, den interpolierten Titel und
    /// das Variablen-Dict, gegen das es ausgewertet wird.</returns>
    public static IReadOnlyList<(GrafanaPanel Panel, string Title, IReadOnlyDictionary<string, string> Vars)>
        ExpandPanels(GrafanaDashboard dash, IReadOnlyDictionary<string, string> renderVars)
    {
        var slots = new List<(GrafanaPanel, string, IReadOnlyDictionary<string, string>)>();
        foreach (var p in dash.Panels.OrderBy(x => x.GridPos.Y).ThenBy(x => x.GridPos.X))
        {
            foreach (var vars2 in ExpandRepeatVars(p, renderVars))
            {
                string title = GrafanaTemplating.Interpolate(p.Title, vars2);
                slots.Add((p, title, vars2));
            }
        }
        return slots;
    }

    /// <summary>
    /// Liefert die Variablen-Dicts, gegen die ein Panel ausgewertet wird: bei
    /// <c>repeat</c> über einer Multi-Variablen eine Kopie pro gewähltem Wert
    /// (Repeat-Expansion), sonst das Basis-Dict einmal. So wird z. B.
    /// <c>${percentile:value}</c> in <c>histogram_quantile(…)</c> zu einem
    /// einzelnen Skalar statt zur Regex-Alternation.
    /// </summary>
    public static IEnumerable<IReadOnlyDictionary<string, string>>
        ExpandRepeatVars(GrafanaPanel p, IReadOnlyDictionary<string, string> baseVars)
    {
        if (string.IsNullOrEmpty(p.Repeat) ||
            !baseVars.TryGetValue(p.Repeat, out var sel) || string.IsNullOrEmpty(sel))
        { yield return baseVars; yield break; }
        var values = sel.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length <= 1) { yield return baseVars; yield break; }
        foreach (var v in values)
        {
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in baseVars) copy[kv.Key] = kv.Value;
            copy[p.Repeat] = v.Trim();
            yield return copy;
        }
    }
}