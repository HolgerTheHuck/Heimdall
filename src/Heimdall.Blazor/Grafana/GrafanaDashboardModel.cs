using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Heimdall.Blazor.Grafana;

// ---------------------------------------------------------------------------
// Internes Modell eines importierten Grafana-Dashboards. Der Parser ist
// bewusst lenient: fehlende Felder werden zu Defaults, unbekannte Felder
// ignoriert, kaputtes JSON liefert null — die UI darf durch ein Dashboard-
// JSON niemals gelegt werden (gleiche Philosophie wie HeimdallCharting).
// Es werden nur die Felder gehoben, die der Heimdall-Renderer tatsächlich
// auswertet (PromQL-Targets, gridPos, Panel-Typ, Thresholds, Templating,
// Zeitfenster). Grafana-spezifische Felder jenseits davon fallen weg.
// ---------------------------------------------------------------------------

/// <summary>Ein importiertes Grafana-Dashboard (vereinfacht auf das fuer Heimdall Noetige).</summary>
public sealed record GrafanaDashboard(
    string Uid,
    string Title,
    IReadOnlyList<GrafanaPanel> Panels,
    IReadOnlyList<GrafanaTemplatingVar> Templating,
    string? TimeFrom,
    string? TimeTo)
{
    /// <summary>Leeres Dashboard (Fallback, falls Panels fehlen).</summary>
    public static GrafanaDashboard Empty { get; } = new(string.Empty, string.Empty,
        Array.Empty<GrafanaPanel>(), Array.Empty<GrafanaTemplatingVar>(), null, null);
}

/// <summary>Ein einzelnes Dashboard-Panel.</summary>
public sealed record GrafanaPanel(
    int Id,
    string Title,
    string Type,
    GrafanaGridPos GridPos,
    IReadOnlyList<GrafanaTarget> Targets,
    GrafanaFieldConfig? FieldConfig,
    IReadOnlyList<GrafanaTransformation>? Transformations,
    /// <summary>Name der Template-Variablen, über die das Panel wiederholt wird
    /// (Grafana <c>repeat</c>), oder null. Heimdall expandiert das Panel pro
    /// gewähltem Variablenwert zu einer eigenen Kachel.</summary>
    string? Repeat = null,
    /// <summary>Normalisierter Datasource-Typ (z. B. <c>prometheus</c>, <c>loki</c>).
    /// Default <c>prometheus</c>; nur PromQL-Panels werden ausgewertet, andere
    /// Datasources (Loki &amp;c.) werden mit Hinweis übersprungen.</summary>
    string DatasourceType = "prometheus",
    /// <summary>Grafana stat-Panel <c>options.graphMode</c> (<c>"area"</c>/
    /// <c>"line"</c>/<c>"none"</c>), oder null. <c>"area"</c>/<c>"line"</c> →
    /// der Renderer gibt pro Serie eine Kachel mit Mini-Graph (Sparkline) aus,
    /// auch bei nur EINER Serie (wie in Grafana); <c>"none"</c>/null → großer
    /// Einzelwert ohne Graph.</summary>
    string? StatGraphMode = null)
{
    /// <summary>Leeres Panel (Fallback).</summary>
    public static GrafanaPanel Empty { get; } = new(0, string.Empty, string.Empty,
        GrafanaGridPos.Zero, Array.Empty<GrafanaTarget>(), null, null);

    /// <summary>Normalisierter Panel-Typ (Groß-/Kleinschreibung ignoriert).</summary>
    public GrafanaPanelKind Kind => GrafanaDashboardModel.KindOf(Type);

    /// <summary><c>true</c>, wenn das stat-Panel eine Sparkline-Graph-Kachel je
    /// Serie zeigen soll (Grafana <c>graphMode = "area"</c> oder <c>"line"</c>).</summary>
    public bool WantsStatGraph =>
        string.Equals(StatGraphMode, "area", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(StatGraphMode, "line", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Raster-Position im 24-Spalten-Grid.</summary>
public sealed record GrafanaGridPos(int X, int Y, int W, int H)
{
    /// <summary>Null-Position (oben links, 1x1).</summary>
    public static GrafanaGridPos Zero { get; } = new(0, 0, 1, 1);
}

/// <summary>Ein PromQL-Target innerhalb eines Panels.</summary>
public sealed record GrafanaTarget(
    string Expr,
    string? LegendFormat,
    string? RefId,
    bool Instant,
    string? Format);

/// <summary>Feldkonfiguration (Einheit + Schwellen) eines Panels.</summary>
public sealed record GrafanaFieldConfig(
    string? Unit,
    IReadOnlyList<GrafanaThresholdStep>? Thresholds);

/// <summary>Ein einzelner Threshold-Schritt (null-Wert = Basis).</summary>
public sealed record GrafanaThresholdStep(double? Value, string Color);

/// <summary>Template-Variable (z. B. <c>$job</c>, <c>$http_route</c>).</summary>
public sealed record GrafanaTemplatingVar(
    string Name,
    string Type,
    string Query,
    string? CurrentValue,
    bool IncludeAll,
    bool Multi);

/// <summary>Transformation eines Panels (z. B. <c>organize</c>); Optionen roh.</summary>
public sealed record GrafanaTransformation(
    string Id,
    IReadOnlyDictionary<string, JsonElement>? Options);

/// <summary>Normalisierter Panel-Typ, den der Renderer unterscheidet.</summary>
public enum GrafanaPanelKind
{
    /// <summary>Zeitreihe (Linie).</summary>
    Timeseries,
    /// <summary>Einzelwert-Kachel.</summary>
    Stat,
    /// <summary>Tabelle.</summary>
    Table,
    /// <summary>Balken-Gauge.</summary>
    BarGauge,
    /// <summary>Kreis-Gauge (Bogen).</summary>
    Gauge,
    /// <summary>Torten-/Kreisdiagramm.</summary>
    Pie,
    /// <summary>Row-Section-Header (keine Daten, nur Überschrift — wird als
    /// Vollbreite-Überschrift gerendert, nicht als Panel-Box).</summary>
    Row,
    /// <summary>Logs-Panel (Loki-Datasource). Heimdall wertet diese gegen den
    /// eigenen Log-Store (<c>IHeimdallQuery</c>) aus, nicht über Loki — der
    /// LogQL-Ausdruck wird auf <c>LogSearch</c> + in-Memory-Filter abgebildet.</summary>
    Logs,
    /// <summary>Heatmap (2D: Zeit × Histogramm-Bucket, Farbintensität = Rate).
    /// Typisch für Antwortzeit-Verteilungen; Daten via <c>…_bucket</c>-Reihen
    /// mit <c>le</c>-Label (kumulativ → inkrementell umgerechnet).</summary>
    Heatmap,
    /// <summary>Vom Renderer nicht unterstuetzt (Fallback als Zeitreihe).</summary>
    Unknown,
}

/// <summary>
/// Lenienter Parser fuer Grafana-Dashboard-JSON in das interne
/// <see cref="GrafanaDashboard"/>-Modell. Wirft nie — bei kaputtem JSON oder
/// fehlenden Pfaden werden Defaults verwendet und ggf. null zurueckgegeben.
/// </summary>
public static class GrafanaDashboardModel
{
    /// <summary>Parst ein Dashboard-JSON; null bei kaputtem JSON oder leerem Modell.</summary>
    public static GrafanaDashboard? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseRoot(doc.RootElement);
        }
        catch { return null; }
    }

    private static GrafanaDashboard ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return GrafanaDashboard.Empty;

        string uid = Str(root, "uid");
        string title = Str(root, "title");
        var panels = new List<GrafanaPanel>();
        // Grafana speichert Panels entweder flach in "panels" oder verschachtelt in
        // "rows"[].panels (Rows sind Layout-Section-Header mit kollabierten Panels).
        // Typische Community-Dashboards nutzen "rows" → flach zu ladende Panels gehen
        // sonst verloren. Rekursiver Extract vereinigt beide Pfade + verschachtelte
        // Rows (Rows-in-Rows, obwohl unüblich, werden toleriert).
        if (root.TryGetProperty("panels", out var ps) && ps.ValueKind == JsonValueKind.Array)
            CollectPanels(ps, panels);
        if (root.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
            CollectPanelsFromRows(rs, panels);

        var templating = new List<GrafanaTemplatingVar>();
        if (root.TryGetProperty("templating", out var tpl) &&
            tpl.TryGetProperty("list", out var tl) && tl.ValueKind == JsonValueKind.Array)
            foreach (var v in tl.EnumerateArray())
            {
                var tv = ParseTemplatingVar(v);
                if (tv is not null) templating.Add(tv);
            }

        string? timeFrom = null, timeTo = null;
        if (root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            timeFrom = StrOrNull(t, "from");
            timeTo = StrOrNull(t, "to");
        }

        if (string.IsNullOrEmpty(uid)) uid = GenerateUid(title, panels.Count);
        return new GrafanaDashboard(uid, title, panels, templating, timeFrom, timeTo);
    }

    private static GrafanaPanel? ParsePanel(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        int id = Int(p, "id");
        string title = Str(p, "title");
        string type = Str(p, "type");
        var grid = ParseGrid(p);
        var targets = new List<GrafanaTarget>();
        if (p.TryGetProperty("targets", out var ts) && ts.ValueKind == JsonValueKind.Array)
            foreach (var t in ts.EnumerateArray())
            {
                var tg = ParseTarget(t);
                if (tg is not null) targets.Add(tg);
            }
        var field = ParseFieldConfig(p);
        var transforms = ParseTransformations(p);
        string? repeat = StrOrNull(p, "repeat");
        string dsType = ParseDatasourceType(p);
        string? graphMode = null;
        if (p.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object
            && opts.TryGetProperty("graphMode", out var gm) && gm.ValueKind == JsonValueKind.String)
            graphMode = gm.GetString();
        return new GrafanaPanel(id, title, type, grid, targets, field, transforms, repeat, dsType, graphMode);
    }

    /// <summary>
    /// Sammelt Panels aus einem flachen <c>"panels"</c>-Array. Panels vom Typ
    /// <c>"row"</c> werden übersprungen (Row-Header sind Layout, keine Panels).
    /// </summary>
    private static void CollectPanels(JsonElement array, List<GrafanaPanel> sink)
    {
        foreach (var p in array.EnumerateArray())
        {
            var panel = ParsePanel(p);
            if (panel is not null && panel.Kind != GrafanaPanelKind.Row) sink.Add(panel);
        }
    }

    /// <summary>
    /// Sammelt Panels aus einem <c>"rows"</c>-Array. Jede Row kann neben
    /// eigenen Feldern (Titel, Layout) ein <c>"panels"</c>-Array enthalten
    /// (typische Community-Dashboards: kollabierte Sektionen). Verschachtelte
    /// Rows-in-Rows werden toleriert (rekursiver Abstieg, max. Tiefe als
    /// Schutz gegen zyklische Quellen).
    /// </summary>
    private static void CollectPanelsFromRows(JsonElement rows, List<GrafanaPanel> sink, int depth = 0)
    {
        if (depth > 8) return;   // Zyklen-/Bombenschutz
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            // Row kann selbst ein panel-ähnliches Objekt sein (gridPos/title).
            if (r.TryGetProperty("panels", out var ps) && ps.ValueKind == JsonValueKind.Array)
                CollectPanels(ps, sink);
            // Toleranz: Row-in-Row.
            if (r.TryGetProperty("rows", out var nested) && nested.ValueKind == JsonValueKind.Array)
                CollectPanelsFromRows(nested, sink, depth + 1);
        }
    }

    /// <summary>
    /// Liest den Datasource-Typ eines Panels. Grafana speichert ihn als
    /// <c>{"type":"prometheus"|"loki"|…}</c>; ältere Dashboards als nackten
    /// String (→ <c>prometheus</c>). Fehlt die Datasource → <c>prometheus</c>
    /// (Default, damit Panels ohne Angabe gerendert werden).
    /// </summary>
    private static string ParseDatasourceType(JsonElement p)
    {
        if (!p.TryGetProperty("datasource", out var ds)) return "prometheus";
        if (ds.ValueKind == JsonValueKind.Object && ds.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() ?? "prometheus";
        return "prometheus";   // Legacy-String oder unrecognized → PromQL-Default
    }

    private static GrafanaGridPos ParseGrid(JsonElement p)
    {
        if (!p.TryGetProperty("gridPos", out var g) || g.ValueKind != JsonValueKind.Object)
            return GrafanaGridPos.Zero;
        return new GrafanaGridPos(Int(g, "x"), Int(g, "y"), IntOr(g, "w", 6), IntOr(g, "h", 8));
    }

    private static GrafanaTarget? ParseTarget(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return null;
        string expr = Str(t, "expr");
        if (string.IsNullOrWhiteSpace(expr)) return null;
        return new GrafanaTarget(
            expr,
            StrOrNull(t, "legendFormat"),
            StrOrNull(t, "refId"),
            Bool(t, "instant"),
            StrOrNull(t, "format"));
    }

    private static GrafanaFieldConfig? ParseFieldConfig(JsonElement p)
    {
        if (!p.TryGetProperty("fieldConfig", out var fc) || fc.ValueKind != JsonValueKind.Object) return null;
        if (!fc.TryGetProperty("defaults", out var def) || def.ValueKind != JsonValueKind.Object) return null;
        string? unit = StrOrNull(def, "unit");
        var steps = ParseThresholds(def);
        return new GrafanaFieldConfig(unit, steps);
    }

    private static IReadOnlyList<GrafanaThresholdStep>? ParseThresholds(JsonElement def)
    {
        if (!def.TryGetProperty("thresholds", out var th) || th.ValueKind != JsonValueKind.Object) return null;
        if (!th.TryGetProperty("steps", out var st) || st.ValueKind != JsonValueKind.Array) return null;
        var list = new List<GrafanaThresholdStep>();
        foreach (var s in st.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            double? val = null;
            if (s.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
                val = d;
            string color = Str(s, "color");
            list.Add(new GrafanaThresholdStep(val, color));
        }
        return list.Count == 0 ? null : list;
    }

    private static IReadOnlyList<GrafanaTransformation>? ParseTransformations(JsonElement p)
    {
        if (!p.TryGetProperty("transformations", out var ts) || ts.ValueKind != JsonValueKind.Array) return null;
        var list = new List<GrafanaTransformation>();
        foreach (var t in ts.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            string id = Str(t, "id");
            IReadOnlyDictionary<string, JsonElement>? opts = null;
            if (t.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var kp in o.EnumerateObject()) dict[kp.Name] = kp.Value.Clone();
                opts = dict;
            }
            list.Add(new GrafanaTransformation(id, opts));
        }
        return list.Count == 0 ? null : list;
    }

    private static GrafanaTemplatingVar? ParseTemplatingVar(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        string name = Str(v, "name");
        if (string.IsNullOrEmpty(name)) return null;
        string type = Str(v, "type");
        string query = Str(v, "query");
        // current kann {"text","value"} sein; value kann string oder Array sein.
        string? current = null;
        if (v.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Object)
            current = CurrentValue(cur);
        bool includeAll = Bool(v, "includeAll");
        bool multi = Bool(v, "multi");
        return new GrafanaTemplatingVar(name, type, query, current, includeAll, multi);
    }

    private static string? CurrentValue(JsonElement cur)
    {
        if (cur.TryGetProperty("value", out var val))
        {
            return val.ValueKind switch
            {
                JsonValueKind.String => val.GetString(),
                JsonValueKind.Number => val.GetRawText(),
                JsonValueKind.Array => JoinArray(val),
                _ => null,
            };
        }
        return null;
    }

    private static string JoinArray(JsonElement arr)
    {
        var parts = new List<string>();
        foreach (var e in arr.EnumerateArray())
        {
            string? s = e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
            if (s is not null) parts.Add(s);
        }
        return string.Join(",", parts);
    }

    /// <summary>Mapt einen Grafana-Panel-Typ-String auf <see cref="GrafanaPanelKind"/>.</summary>
    public static GrafanaPanelKind KindOf(string type)
    {
        return (type ?? string.Empty).ToLowerInvariant() switch
        {
            "timeseries" or "graph" => GrafanaPanelKind.Timeseries,
            "stat" or "singlestat" => GrafanaPanelKind.Stat,
            "table" => GrafanaPanelKind.Table,
            "bargauge" => GrafanaPanelKind.BarGauge,
            "gauge" => GrafanaPanelKind.Gauge,
            "pie" or "piechart" => GrafanaPanelKind.Pie,
            "row" => GrafanaPanelKind.Row,
            "logs" => GrafanaPanelKind.Logs,
            "heatmap" => GrafanaPanelKind.Heatmap,
            _ => GrafanaPanelKind.Unknown,
        };
    }

    private static string GenerateUid(string title, int panels)
    {
        // Stabiler Fallback aus Titel + Panelzahl, damit Re-Import denselben
        // Dateinamen ergibt (idempotent). Prefix "d" sorgt fuer Dateinamen ohne
        // fuehrende Ziffer.
        int h = unchecked(17 * 31 + (title ?? string.Empty).GetHashCode() * 31 + panels);
        return "d" + (h & 0x7FFFFFFF).ToString("x");
    }

    // --- JSON-Helfer (werfen nie) ---
    private static string Str(JsonElement e, string name) => StrOrNull(e, name) ?? string.Empty;
    private static string? StrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name) => IntOr(e, name, 0);
    private static int IntOr(JsonElement e, string name, int dflt)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return dflt;
        return v.TryGetInt32(out var i) ? i : (int)GetInt64Safe(v, dflt);
    }
    private static long GetInt64Safe(JsonElement v, long dflt) =>
        v.TryGetInt64(out var l) ? l : dflt;
    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}