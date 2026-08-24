using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Heimdall;
using Heimdall.Prometheus;

namespace Heimdall.Blazor.Grafana;

// ---------------------------------------------------------------------------
// Wertet die PromQL-Targets eines Panels ueber die <see cref="PromEngine"/>
/// in-process aus und uebersetzt das <see cref="PromResult"/> in ein
/// renderfertiges Modell (Chart/Serien, Stat, Tabelle, Gauge, Bargauge, Pie).
/// Rein und werferfrei: jedes Eval-Problem wird zu <see cref="ErrorResult"/>,
/// nie geworfen — die UI darf durch ein boeses Panel nicht gelegt werden.
/// Zeitstempel der Engine sind Millisekunden; das Heimdall-Chart erwartet
/// Nanosekunden (×1_000_000).
// ---------------------------------------------------------------------------

/// <summary>Ergebnis-Tripel: Panel + renderfertiges Resultat.</summary>
public sealed record RenderedPanel(GrafanaPanel Panel, PanelRenderResult Result);

/// <summary>Basistyp der Render-Resultate (Union).</summary>
public abstract record PanelRenderResult;

/// <summary>Zeitreihen-Panels (Linien) — Series fuer <c>HeimdallChart</c>.</summary>
public sealed record ChartResult(IReadOnlyList<ChartSeries> Series, string? Unit) : PanelRenderResult;
/// <summary>Einzelwert-Kachel (stat) — Wert fuer <c>HeimdallKpi</c>.</summary>
public sealed record StatResult(double Value, string? DisplayText, string Tone, string? Unit) : PanelRenderResult;
/// <summary>Stat-Kachel für Multi-Serien-Stat (Grafana <c>graphMode=area</c>):
/// Label (unterscheidende Label-Werte der Serie) + letzter Wert + Sparkline-
/// Punkte (Tms,V) für den Mini-Flächen-Graph. <c>RawValue</c> für Sortierung.</summary>
public sealed record StatTile(
    string Label, string DisplayText, string Tone, double RawValue, IReadOnlyList<(long Tms, double V)> Points);
/// <summary>Multi-Serien-Stat: eine Kachel je Serie (z. B. pro
/// <c>http_response_status_code</c>), jeweils mit Mini-Flächen-Graph — entspricht
/// Grafanas stat-Panel mit <c>graphMode=area</c>/<c>wideLayout</c>.</summary>
public sealed record StatGridResult(IReadOnlyList<StatTile> Tiles, string? Unit) : PanelRenderResult;
/// <summary>Tabellen-Panels — Spalten + Zeilen, optional pro Zelle ein Daten-Link
/// (<c>LinkUrls</c>, gleiche Shape wie <c>Rows</c>; null/leer = kein Link).
/// <c>LinkUrls[r][c]</c> trägt die bereits vollständig aufgelöste href (inkl.
/// BasePath) + den pro Zeile interpolierten Titel (Tooltip).</summary>
public sealed record TableResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<IReadOnlyList<TableCellLink?>>? LinkUrls = null) : PanelRenderResult;

/// <summary>Aufgelöster Daten-Link einer Tabellenzelle: href (inkl. BasePath) + Titel.</summary>
public sealed record TableCellLink(string Href, string? Title);
/// <summary>Kreis-Gauge — Wert/Min/Max/Farbe/Tone.</summary>
public sealed record GaugeResult(double Value, double Min, double Max, string Color, string Tone, string? Unit) : PanelRenderResult;
/// <summary>Bargauge-Panels — Liste von Balken.</summary>
public sealed record BarGaugeResult(IReadOnlyList<BarGaugeRow> Rows, string? Unit) : PanelRenderResult;
/// <summary>Kreisdiagramm — Liste von Scheiben.</summary>
public sealed record PieResult(IReadOnlyList<PieSlice> Slices, string? Unit) : PanelRenderResult;
/// <summary>PromQL konnte nicht ausgewertet werden.</summary>
public sealed record ErrorResult(string Message) : PanelRenderResult;
/// <summary>Auswertung ok, aber ohne Daten (leere Matrix/Vektor).</summary>
public sealed record EmptyResult(string Message) : PanelRenderResult;
/// <summary>Row-Section-Header (keine Daten, nur Überschrift — Vollbreite).</summary>
public sealed record RowResult : PanelRenderResult;
/// <summary>Logs-Panel: Trefferzeilen aus Heimdalls eigenem Log-Store.
/// <c>TruncatedCount</c> = Anzahl furtherer Treffer jenseits des Anzeige-Limits.</summary>
public sealed record LogResult(IReadOnlyList<LogRow> Rows, int TruncatedCount) : PanelRenderResult;
/// <summary>Heatmap-Panel: Buckets (aufsteigend nach Obergrenze, letztes = +Inf),
/// Zeitspalten in ms, Maximal-Zellwert (für Farb-Skalierung) + Einheit.</summary>
public sealed record HeatmapResult(
    IReadOnlyList<HeatmapBucket> Buckets, IReadOnlyList<long> ColumnTimesMs, double MaxValue, string? Unit) : PanelRenderResult;

/// <summary>
/// Statischer Renderer: wertet ein Panel aus und liefert das passende
/// <see cref="PanelRenderResult"/>. Wirft nie.
/// </summary>
internal static class GrafanaPanelRenderer
{
    /// <summary>
    /// Wertet <paramref name="panel"/> aus und uebersetzt das Ergebnis.
    /// <paramref name="stepMs"/> wird nur fuer Range-Panels (timeseries) benoetigt.
    /// <paramref name="query"/> ist der Heimdall-Log-Store; nur fuer Logs-Panels
    /// (Loki-Datasource) erforderlich — fehlt er, liefert ein Logs-Panel einen
    /// Hinweis statt zu crashen. PromQL-Panels ignorieren ihn.
    /// </summary>
    public static RenderedPanel Render(
        GrafanaPanel panel,
        PromEngine engine,
        long fromMs,
        long toMs,
        long stepMs,
        IReadOnlyDictionary<string, string> vars,
        IHeimdallQuery? query = null,
        string? lang = null,
        string basePath = "/otel")
    {
        // Row-Section-Header: keine Auswertung, nur Überschrift (Vollbreite in der UI).
        if (panel.Kind == GrafanaPanelKind.Row)
            return new RenderedPanel(panel, new RowResult());

        var interp = vars ?? new Dictionary<string, string>(0);
        try
        {
            // Logs-Panels (Loki-Datasource) werden gegen Heimdalls eigenen Log-Store
            // ausgewertet — vor der PromQL-Datasource-Weige, da Logs Panels loki-typisch
            // sind und kein PromQL enthalten (früher: Parse-Crash am '|='-Operator).
            if (panel.Kind == GrafanaPanelKind.Logs)
            {
                if (query is null)
                    return new RenderedPanel(panel,
                        new ErrorResult(HeimdallI18n.T(lang, "grafana.err.logsNoStore")));
                return new RenderedPanel(panel, RenderLogs(panel, query, fromMs, toMs, interp, lang));
            }

            // Nicht-PromQL-Datasources (Loki &c.) kann Heimdall für Nicht-Logs-Panels
            // nicht auswerten — klarer Hinweis statt PromQL-Parse-Crash.
            if (!string.Equals(panel.DatasourceType, "prometheus", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(panel.DatasourceType))
                return new RenderedPanel(panel,
                    new ErrorResult(HeimdallI18n.T(lang, "grafana.err.unsupportedDatasource", panel.DatasourceType)));

            if (panel.Targets.Count == 0)
                return new RenderedPanel(panel, new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noTargets")));

            return panel.Kind switch
            {
                GrafanaPanelKind.Timeseries => new RenderedPanel(panel, RenderTimeseries(panel, engine, fromMs, toMs, stepMs, interp, lang)),
                GrafanaPanelKind.Stat => new RenderedPanel(panel, RenderStat(panel, engine, fromMs, toMs, stepMs, interp, lang)),
                GrafanaPanelKind.Table => new RenderedPanel(panel, RenderTable(panel, engine, fromMs, toMs, interp, lang, basePath)),
                GrafanaPanelKind.Gauge => new RenderedPanel(panel, RenderGauge(panel, engine, toMs, interp, lang)),
                GrafanaPanelKind.BarGauge => new RenderedPanel(panel, RenderBarGauge(panel, engine, toMs, interp, lang)),
                GrafanaPanelKind.Pie => new RenderedPanel(panel, RenderPie(panel, engine, toMs, interp, lang)),
                GrafanaPanelKind.Heatmap => new RenderedPanel(panel, RenderHeatmap(panel, engine, fromMs, toMs, stepMs, interp, lang)),
                _ => new RenderedPanel(panel, RenderTimeseries(panel, engine, fromMs, toMs, stepMs, interp, lang)),
            };
        }
        catch (Exception ex)
        {
            return new RenderedPanel(panel,
                new ErrorResult(HeimdallI18n.T(lang, "grafana.err.panel", panel.Title, ex.Message)));
        }
    }

    // === Timeseries ========================================================
    private static PanelRenderResult RenderTimeseries(
        GrafanaPanel panel, PromEngine engine, long fromMs, long toMs, long stepMs, IReadOnlyDictionary<string, string> vars, string? lang)
    {
        var series = new List<ChartSeries>();
        int colorIdx = 0;
        foreach (var t in panel.Targets)
        {
            var expr = GrafanaTemplating.Interpolate(t.Expr, vars);
            var res = engine.EvalRange(expr, fromMs, toMs, stepMs);
            if (res.Kind != PromResultKind.Matrix || res.Matrix is null) continue;
            foreach (var rs in res.Matrix.Series)
            {
                var pts = new List<(long T, double V)>(rs.Points.Count);
                foreach (var p in rs.Points) pts.Add((p.TimestampMs * 1_000_000L, p.Value));
                if (pts.Count == 0) continue;
                var label = LegendFor(t.LegendFormat, rs.Labels);
                series.Add(new ChartSeries(label, HeimdallCharting.ColorAt(colorIdx++), pts));
            }
        }
        var unit = panel.FieldConfig?.Unit;
        return series.Count == 0
            ? new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noData"))
            : new ChartResult(series, unit);
    }

    // === Stat ==============================================================
    // Grafana-stat-Panel mit EINER Serie/Skalar → StatResult (großer Wert, HeimdallKpi).
    // Mit MEHREREN Serien (z. B. `sum by (http_response_status_code)`) → StatGridResult:
    // eine Kachel je Serie mit Mini-Flächen-Graph (graphMode=area, wideLayout).
    // Grafana graphMode=area/line (panel.WantsStatGraph) erzwingt die Kachel-Ansicht
    // AUCH bei nur EINER Serie (Sparkline + Wert) — wie in Grafana, nicht als bloße Zahl.
    private static PanelRenderResult RenderStat(
        GrafanaPanel panel, PromEngine engine, long fromMs, long toMs, long stepMs, IReadOnlyDictionary<string, string> vars, string? lang)
    {
        var target = panel.Targets[0];
        var expr = GrafanaTemplating.Interpolate(target.Expr, vars);

        // Instant-Vektor am Fensterende entscheidet über Single- vs. Multi-Serien-Stat.
        var instSamples = ExtractSamples(engine.EvalInstant(expr, toMs));

        // Single-Value-Stat (Skalar/eine Serie): bewährter Pfad — unverändert.
        // Ausnahme: stat-Panel mit graphMode=area/line → immer Kachel+Sparkline (s. u.).
        if (!panel.WantsStatGraph && instSamples.Count <= 1)
        {
            var (value, ok) = EvalScalar(target, engine, toMs, vars);
            if (!ok) return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noValue"));
            var (tone, _) = ThresholdTone(panel.FieldConfig, value);
            return new StatResult(value, HeimdallCharting.FmtValue(value), tone, panel.FieldConfig?.Unit);
        }

        // (Multi-Serien-Stat ODER graphMode=area/line): pro Serie eine Kachel mit
        // Mini-Flächen-Graph — auch bei nur EINER Serie (Sparkline + letzter Wert).
        var res = engine.EvalRange(expr, fromMs, toMs, stepMs);
        if (res.Kind != PromResultKind.Matrix || res.Matrix is null || res.Matrix.Series.Count == 0)
            return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noValues"));

        var tiles = new List<StatTile>();
        foreach (var rs in res.Matrix.Series)
        {
            var pts = new List<(long Tms, double V)>();
            double lastV = double.NaN;
            foreach (var p in rs.Points)
            {
                if (!double.IsFinite(p.Value)) continue;
                pts.Add((p.TimestampMs, p.Value));
                lastV = p.Value;            // lastNotNull (Grafana reduceOptions.calcs)
            }
            if (double.IsNaN(lastV)) continue;
            var (tone, _) = ThresholdTone(panel.FieldConfig, lastV);
            tiles.Add(new StatTile(SeriesLabel(rs.Labels, panel.Title),
                HeimdallCharting.FmtValue(lastV), tone, lastV, pts));
        }
        if (tiles.Count == 0)
            return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noObserved"));
        tiles.Sort((a, b) => b.RawValue.CompareTo(a.RawValue));   // topk-Reihenfolge (größte zuerst)
        return new StatGridResult(tiles, panel.FieldConfig?.Unit);
    }

    /// <summary>Tile-Label: Werte aller Nicht-<c>__name__</c>-Labels, mit Leerzeichen
    /// joined (z. B. "200" bzw. "GET /api/orders 200"). Ohne Labels → Panel-Titel.</summary>
    private static string SeriesLabel(IReadOnlyDictionary<string, string> labels, string fallback)
    {
        var parts = new List<string>();
        foreach (var kv in labels)
            if (kv.Key != "__name__" && !string.IsNullOrEmpty(kv.Value)) parts.Add(kv.Value);
        return parts.Count > 0 ? string.Join(" ", parts) : fallback;
    }

    // === Table =============================================================
    private static PanelRenderResult RenderTable(
        GrafanaPanel panel, PromEngine engine, long fromMs, long toMs,
        IReadOnlyDictionary<string, string> vars, string? lang, string basePath)
    {
        var target = panel.Targets[0];
        var expr = GrafanaTemplating.Interpolate(target.Expr, vars);
        // Table-Panels sind in der Regel instant; falls nicht, evaluiere instant am Fensterende.
        var res = engine.EvalInstant(expr, toMs);
        var samples = ExtractSamples(res);
        if (samples.Count == 0) return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noTableRows"));

        // organize-Transformation (renameByName / excludeByName) best-effort anwenden.
        var (rename, exclude) = ParseOrganize(panel.Transformations);

        // Spalten: Label-Keys (ohne __name__) + "Value", umbenannt/ausgeschlossen.
        var labelKeys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in samples)
            foreach (var kv in s.Labels)
                if (kv.Key != "__name__" && seen.Add(kv.Key)) labelKeys.Add(kv.Key);

        var columns = new List<string>();
        var colIndex = new List<int>();     // Index in labelKeys je angezeigter Spalte
        foreach (var k in labelKeys)
        {
            if (exclude.Contains(k, StringComparer.OrdinalIgnoreCase)) continue;
            columns.Add(rename.TryGetValue(k, out var rn) && !string.IsNullOrEmpty(rn) ? rn : k);
            colIndex.Add(labelKeys.IndexOf(k));
        }
        bool hasValueCol = !exclude.Contains("Value", StringComparer.OrdinalIgnoreCase);
        if (hasValueCol)
            columns.Add(rename.TryGetValue("Value", out var vn) && !string.IsNullOrEmpty(vn) ? vn : "Value");

        // Daten-Links (fieldConfig.overrides → byName) pro angezeigter Spalte.
        // Der byName-Matcher trifft den DISPLAY-Namen; die URL referenziert per
        // ${__data.fields.X} aber den ORIGINAL-Feldnamen (Label-Key) — beide sind
        // hier vorhanden (columns[i] = Display, labelKeys[colIndex[i]] = Original).
        var links = panel.FieldConfig?.LinksByColumn;
        // colLinks[i] = Links für Spalte i (Display-Name columns[i]), oder null.
        IReadOnlyList<GrafanaDataLink>?[] colLinks = new IReadOnlyList<GrafanaDataLink>?[columns.Count];
        bool anyLinks = false;
        if (links is not null)
        {
            for (int i = 0; i < columns.Count; i++)
                if (links.TryGetValue(columns[i], out var cl) && cl.Count > 0)
                {
                    colLinks[i] = cl;
                    anyLinks = true;
                }
        }

        var rows = new List<IReadOnlyList<string>>();
        List<List<TableCellLink?>>? linkUrls = anyLinks ? new List<List<TableCellLink?>>(samples.Count) : null;
        foreach (var s in samples)
        {
            var row = new List<string>();
            // Feldwerte der Zeile (Original-Labelnamen → Wert) für ${__data.fields.X}.
            // Nur aufbauen, wenn überhaupt Links vorhanden sind.
            Dictionary<string, string>? fieldValues = anyLinks
                ? new Dictionary<string, string>(s.Labels.Count + 1, StringComparer.Ordinal)
                : null;
            if (fieldValues is not null)
            {
                foreach (var kv in s.Labels) fieldValues[kv.Key] = kv.Value;
                fieldValues["Value"] = HeimdallCharting.FmtValue(s.Value);
            }

            List<TableCellLink?>? rowLinks = anyLinks ? new List<TableCellLink?>(columns.Count) : null;
            for (int i = 0; i < colIndex.Count; i++)
            {
                var key = labelKeys[colIndex[i]];
                row.Add(s.Labels.TryGetValue(key, out var v) ? v : "");
                rowLinks?.Add(ResolveCellLink(colLinks[i], fieldValues!, vars, fromMs, toMs, basePath));
            }
            if (hasValueCol)
            {
                row.Add(HeimdallCharting.FmtValue(s.Value));
                // Value-Spalten-Index = columns.Count - 1 (zuletzt angehängt).
                rowLinks?.Add(ResolveCellLink(colLinks[columns.Count - 1], fieldValues!, vars, fromMs, toMs, basePath));
            }
            rows.Add(row);
            linkUrls?.Add(rowLinks!);
        }
        return new TableResult(columns, rows, linkUrls);
    }

    /// <summary>Löst die ERSTE Link-URL + Titel einer Zelle auf (oder null falls keine Links).</summary>
    private static TableCellLink? ResolveCellLink(
        IReadOnlyList<GrafanaDataLink>? cellLinks, IReadOnlyDictionary<string, string> fieldValues,
        IReadOnlyDictionary<string, string> vars, long fromMs, long toMs, string basePath)
    {
        if (cellLinks is null || cellLinks.Count == 0) return null;
        var link = cellLinks[0];
        string href = GrafanaTemplating.InterpolateLinkUrl(link.Url, fieldValues, vars, fromMs, toMs, basePath);
        string? title = string.IsNullOrEmpty(link.Title) ? null
            : GrafanaTemplating.InterpolateLinkTitle(link.Title, fieldValues);
        return new TableCellLink(href, title);
    }

    // === Gauge =============================================================
    private static PanelRenderResult RenderGauge(
        GrafanaPanel panel, PromEngine engine, long toMs, IReadOnlyDictionary<string, string> vars, string? lang)
    {
        var (value, ok) = EvalScalar(panel.Targets[0], engine, toMs, vars);
        if (!ok) return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noGauge"));
        var (tone, color) = ThresholdTone(panel.FieldConfig, value);
        double max = MaxForGauge(panel.FieldConfig, value);
        return new GaugeResult(value, 0, max, color, tone, panel.FieldConfig?.Unit);
    }

    // === Bargauge ==========================================================
    private static PanelRenderResult RenderBarGauge(
        GrafanaPanel panel, PromEngine engine, long toMs, IReadOnlyDictionary<string, string> vars, string? lang)
    {
        var expr = GrafanaTemplating.Interpolate(panel.Targets[0].Expr, vars);
        var samples = ExtractSamples(engine.EvalInstant(expr, toMs));
        if (samples.Count == 0) return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noBarGauge"));
        double max = 0;
        foreach (var s in samples) if (s.Value > max) max = s.Value;
        if (max <= 0) max = 1;
        var rows = new List<BarGaugeRow>();
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var (_, color) = ThresholdTone(panel.FieldConfig, s.Value);
            rows.Add(new BarGaugeRow(LegendFor(panel.Targets[0].LegendFormat, s.Labels), s.Value, max, color, panel.FieldConfig?.Unit));
        }
        return new BarGaugeResult(rows, panel.FieldConfig?.Unit);
    }

    // === Pie ===============================================================
    private static PanelRenderResult RenderPie(
        GrafanaPanel panel, PromEngine engine, long toMs, IReadOnlyDictionary<string, string> vars, string? lang)
    {
        var expr = GrafanaTemplating.Interpolate(panel.Targets[0].Expr, vars);
        var samples = ExtractSamples(engine.EvalInstant(expr, toMs));
        if (samples.Count == 0) return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noPie"));
        var slices = new List<PieSlice>();
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            slices.Add(new PieSlice(LegendFor(panel.Targets[0].LegendFormat, s.Labels), s.Value, HeimdallCharting.ColorAt(i)));
        }
        return new PieResult(slices, panel.FieldConfig?.Unit);
    }

    // === Helfer ============================================================

    // === Heatmap (Zeit × Histogramm-Bucket) ===============================

    /// <summary>
    /// Wertet ein Heatmap-Panel aus: PromQL liefert kumulative Histogramm-Bucket-
    /// Raten pro <c>le</c> (z. B. <c>sum(rate(…_bucket)) by (le)</c>). Heimdall
    /// wandelt kumulativ → inkrementell um (Bucket[i] = cum[le_i] − cum[le_{i−1}]),
    /// baut eine 2D-Matrix (Buckets × Zeitspalten) und liefert sie als
    /// <see cref="HeatmapResult"/> für das server-seitige SVG. Fehlt das <c>le</c>-
    /// Label oder sind alle Zellen ≤ 1e-9, entsteht <see cref="EmptyResult"/>.
    /// </summary>
    private static PanelRenderResult RenderHeatmap(
        GrafanaPanel panel, PromEngine engine, long fromMs, long toMs, long stepMs, IReadOnlyDictionary<string, string> vars, string? lang)
    {
        // Pro le-Bucket eine kumulative Zeitreihe sammeln.
        var byLe = new Dictionary<string, List<RangePoint>>(StringComparer.Ordinal);
        foreach (var t in panel.Targets)
        {
            var expr = GrafanaTemplating.Interpolate(t.Expr, vars);
            var res = engine.EvalRange(expr, fromMs, toMs, stepMs);
            if (res.Kind != PromResultKind.Matrix || res.Matrix is null) continue;
            foreach (var rs in res.Matrix.Series)
            {
                if (!rs.Labels.TryGetValue("le", out var leStr) || string.IsNullOrEmpty(leStr)) continue;
                if (!byLe.TryGetValue(leStr, out var list))
                {
                    list = new List<RangePoint>(rs.Points.Count);
                    byLe[leStr] = list;
                }
                foreach (var p in rs.Points) list.Add(p);
            }
        }
        if (byLe.Count == 0)
            return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noHistBuckets"));

        // le-Werte parsen + aufsteigend sortieren (+Inf ans Ende).
        var raw = new List<(double Le, string Str, List<RangePoint> Pts)>(byLe.Count);
        foreach (var kv in byLe) raw.Add((ParseLe(kv.Key), kv.Key, kv.Value));
        raw.Sort((a, b) => a.Le.CompareTo(b.Le));

        // Vereinigungs-Zeitachse (aufsteigend).
        var timesSet = new SortedSet<long>();
        foreach (var b in raw) foreach (var p in b.Pts) timesSet.Add(p.TimestampMs);
        if (timesSet.Count == 0)
            return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noDataPoints"));
        var times = timesSet.ToArray();

        // Kumulativ → inkrementell: increment[i] = cum[i] − cum[i−1] (Forward-Fill
        // je Spalte). +Inf sortiert zuletzt → increment = cum[+Inf] − cum[letzt. endlich].
        var cum = new double[raw.Count][];
        for (int bi = 0; bi < raw.Count; bi++) cum[bi] = ForwardFill(raw[bi].Pts, times);

        double max = 0;
        var buckets = new List<HeatmapBucket>(raw.Count);
        for (int bi = 0; bi < raw.Count; bi++)
        {
            var inc = new double[times.Length];
            for (int ti = 0; ti < times.Length; ti++)
            {
                double prev = bi == 0 ? 0 : cum[bi - 1][ti];
                double v = cum[bi][ti] - prev;
                if (v < 0) v = 0;            // Raten-Rauschen / Counter-Reset-Konsistenz.
                inc[ti] = v;
                if (v > max) max = v;
            }
            buckets.Add(new HeatmapBucket(raw[bi].Le, HeatmapLabel(raw[bi].Le), inc));
        }

        if (max <= 1e-9)
            return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noObservedZero"));

        return new HeatmapResult(buckets, times, max, panel.FieldConfig?.Unit);
    }

    /// <summary>Parse eines <c>le</c>-Bucket-Strings (+Inf → +∞; ungültig → +∞).</summary>
    private static double ParseLe(string s)
    {
        if (string.Equals(s, "+Inf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "Inf", StringComparison.OrdinalIgnoreCase))
            return double.PositiveInfinity;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.PositiveInfinity;
    }

    /// <summary>Heatmap-Bucket-Label: finite <c>le</c> als Dauer („5 ms", „1 s"),
    /// +Inf als „∞" — entspricht Grafanas <c>renameByRegex</c> $1s, aber lesbarer.</summary>
    private static string HeatmapLabel(double le) =>
        double.IsPositiveInfinity(le) ? "∞" : HeimdallFmt.Dur((long)(le * 1_000_000_000L));

    /// <summary>Forward-Fill der Punkte auf die vereinigte Zeitachse: Wert je Spalte
    /// = letzter Punkt ≤ ts (0 falls keiner) — robust gegen Lücken in Einzelreihen.</summary>
    private static double[] ForwardFill(IReadOnlyList<RangePoint> pts, long[] times)
    {
        var result = new double[times.Length];
        int j = 0;
        double last = 0;
        for (int ti = 0; ti < times.Length; ti++)
        {
            long t = times[ti];
            while (j < pts.Count && pts[j].TimestampMs <= t) { last = pts[j].Value; j++; }
            result[ti] = last;
        }
        return result;
    }

    // === Helfer ============================================================

    // === Logs (Loki → Heimdall-Log-Store) =================================

    /// <summary>
    /// Wertet ein Logs-Panel (Loki-Datasource) gegen Heimdalls eigenen Log-Store
    /// aus. Der LogQL-Ausdruck wird via <see cref="LogQl"/> in Stream-Selector
    /// (Label-Matcher) und Zeilenfilter zerlegt; das erste <c>|=</c> dient als
    /// FTS-Vorfilter (engt das DB-Resultat ein), alle Matcher/Filter werden
    /// in-Memory nachgereicht (Loki-Semantik: <c>|=</c> case-sensitive contains,
    /// <c>|~</c> Regex). Label-Keys werden best-effort gesucht (Loki
    /// <c>service_name</c> ↔ OTel <c>service.name</c>); fehlende Labels
    /// schließen eine Zeile nicht aus, damit Heimdalls flacher Log-Store auch
    /// ohne pro-Log-Resource-Label Treffer liefert. Anzeige-Limit 100.
    /// </summary>
    private static PanelRenderResult RenderLogs(
        GrafanaPanel panel, IHeimdallQuery query, long fromMs, long toMs, IReadOnlyDictionary<string, string> vars, string? lang)
    {
        if (panel.Targets.Count == 0 || string.IsNullOrWhiteSpace(panel.Targets[0].Expr))
            return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noLogQuery"));

        var expr = GrafanaTemplating.Interpolate(panel.Targets[0].Expr, vars);
        var ql = LogQl.Parse(expr);

        // Erstes '|=' (nicht-leer) als FTS-Vorfilter; Regex-/Negativ-Filter sowie
        // Label-Matcher werden in-Memory angewendet (sicher für Loki-Semantik).
        string? fts = null;
        foreach (var f in ql.Lines)
            if (f.Op == "|=" && !string.IsNullOrEmpty(f.Value)) { fts = f.Value; break; }

        var search = new LogSearch
        {
            Text = fts,
            FromUnixNano = fromMs * 1_000_000L,
            ToUnixNano = toMs * 1_000_000L,
            Limit = 500,
        };

        var raw = query.SearchLogs(search);
        var matched = new List<LogRow>(raw.Count);
        foreach (var r in raw)
            if (MatchStream(ql.Stream, r) && MatchLines(ql.Lines, r))
                matched.Add(r);

        if (matched.Count == 0) return new EmptyResult(HeimdallI18n.T(lang, "grafana.empty.noLogs"));

        const int Cap = 100;
        int truncated = Math.Max(0, matched.Count - Cap);
        var shown = truncated == 0 ? (IReadOnlyList<LogRow>)matched : matched.Take(Cap).ToArray();
        return new LogResult(shown, truncated);
    }

    /// <summary>
    /// Prüft eine Log-Zeile gegen die Stream-Selector-Matcher (best-effort:
    /// ein fehlendes Label schließt die Zeile nicht aus — Heimdalls Log-Store
    /// trägt Resource-Labels nicht zwingend pro Zeile).
    /// </summary>
    private static bool MatchStream(IReadOnlyList<LogQlMatcher> matchers, LogRow row)
    {
        if (matchers.Count == 0) return true;
        var attrs = HeimdallCharting.ParseAttrs(row.AttrsJson);
        foreach (var m in matchers)
        {
            string? v = LookupAttr(attrs, m.Key);
            if (v is null) continue;   // Best-Effort: fehlendes Label schließt nicht aus.
            if (!MatchMatcher(m.Op, v, m.Value)) return false;
        }
        return true;
    }

    /// <summary>
    /// Prüft eine Log-Zeile gegen die Zeilenfilter (Loki-Semantik auf dem Body).
    /// </summary>
    private static bool MatchLines(IReadOnlyList<LogQlFilter> filters, LogRow row)
    {
        if (filters.Count == 0) return true;
        string line = row.Body ?? string.Empty;
        foreach (var f in filters)
            if (!MatchLineFilter(f.Op, line, f.Value)) return false;
        return true;
    }

    /// <summary>
    /// Sucht ein Attribut by Key: exakt → case-insensitiv → <c>_</c>/<c>.</c>
    /// normalisiert (Loki <c>service_name</c> ↔ OTel <c>service.name</c>).
    /// </summary>
    private static string? LookupAttr(IReadOnlyList<HeimdallCharting.AttrKv> attrs, string key)
    {
        if (attrs.Count == 0 || string.IsNullOrEmpty(key)) return null;
        foreach (var a in attrs)
            if (string.Equals(a.Key, key, StringComparison.Ordinal)) return a.Value;
        foreach (var a in attrs)
            if (string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)) return a.Value;
        string alt = key.Contains('_') ? key.Replace('_', '.') : key.Replace('.', '_');
        if (alt != key)
            foreach (var a in attrs)
                if (string.Equals(a.Key, alt, StringComparison.OrdinalIgnoreCase)) return a.Value;
        return null;
    }

    private static bool MatchMatcher(string op, string actual, string expected)
        => op switch
        {
            "="  => string.Equals(actual, expected, StringComparison.Ordinal),
            "!=" => !string.Equals(actual, expected, StringComparison.Ordinal),
            "=~" => RegexMatches(actual, expected),
            "!~" => !RegexMatches(actual, expected),
            _    => true,
        };

    private static bool MatchLineFilter(string op, string line, string expected)
        => op switch
        {
            "|=" => line.Contains(expected, StringComparison.Ordinal),
            "!=" => !line.Contains(expected, StringComparison.Ordinal),
            "|~" => RegexMatches(line, expected),
            "!~" => !RegexMatches(line, expected),
            _    => true,
        };

    /// <summary>Regex-Match mit Timeout; bei ungültigem Pattern/sicherem Fehlschlag false.</summary>
    private static bool RegexMatches(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        try
        {
            return Regex.IsMatch(input, pattern,
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch { return false; }
    }

    /// <summary>
    /// Liefert einen skalaren Wert (Stat/Gauge): bei Vektor die Summe aller
    /// Samples, bei Skalar direkt. <paramref name="ok"/>=false bei leer.
    /// </summary>
    private static (double value, bool ok) EvalScalar(
        GrafanaTarget target, PromEngine engine, long toMs, IReadOnlyDictionary<string, string> vars)
    {
        var expr = GrafanaTemplating.Interpolate(target.Expr, vars);
        var res = engine.EvalInstant(expr, toMs);
        if (res.Kind == PromResultKind.Scalar && res.Scalar is not null)
            return (res.Scalar.Value, true);
        var samples = ExtractSamples(res);
        if (samples.Count == 0) return (0, false);
        double sum = 0;
        foreach (var s in samples) sum += s.Value;
        return (sum, true);
    }

    private static IReadOnlyList<Sample> ExtractSamples(PromResult res)
    {
        if (res.Kind == PromResultKind.Vector && res.Vector is not null) return res.Vector.Samples;
        return Array.Empty<Sample>();
    }

    /// <summary>
    /// Bildet eine Legend aus <paramref name="legendFormat"/> (Grafana
    /// <c>{{label}}</c>-Interpolation) oder dem Fingerprint der Serie.
    /// </summary>
    private static string LegendFor(string? legendFormat, SeriesLabels labels)
    {
        if (string.IsNullOrEmpty(legendFormat)) return labels.Fingerprint;
        var sb = new System.Text.StringBuilder(legendFormat.Length);
        int i = 0;
        while (i < legendFormat.Length)
        {
            if (legendFormat[i] == '{' && i + 1 < legendFormat.Length && legendFormat[i + 1] == '{')
            {
                int end = legendFormat.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0) { sb.Append(legendFormat[i]); i++; continue; }
                string key = legendFormat.Substring(i + 2, end - (i + 2));
                if (labels.TryGetValue(key, out var v)) sb.Append(v);
                i = end + 2;
            }
            else { sb.Append(legendFormat[i]); i++; }
        }
        var result = sb.ToString();
        return string.IsNullOrEmpty(result) ? labels.Fingerprint : result;
    }

    /// <summary>
    /// Mappt einen Wert auf (Tone, CSS-Farbe) anhand der Grafana-Threshold-
    /// Steps. Fehlen Thresholds → ("accent", Akzentfarbe). Farben werden
    /// normalisiert: green→ok, yellow/orange→warn, red→err, Rest→accent.
    /// </summary>
    private static (string tone, string color) ThresholdTone(GrafanaFieldConfig? cfg, double value)
    {
        var steps = cfg?.Thresholds;
        if (steps is null || steps.Count == 0) return ("accent", "var(--hmd-accent)");
        string color = steps[0].Color;
        foreach (var st in steps)
            if (st.Value is not null && !double.IsNaN(st.Value.Value) && value >= st.Value.Value)
                color = st.Color;
        return MapColor(color);
    }

    private static (string tone, string color) MapColor(string grafanaColor)
    {
        var c = (grafanaColor ?? string.Empty).ToLowerInvariant();
        if (c.Contains("green") || c == "#37872d" || c == "green") return ("ok", "var(--hmd-ok)");
        if (c.Contains("yellow") || c.Contains("orange") || c == "#e0b400") return ("warn", "var(--hmd-warn)");
        if (c.Contains("red") || c == "#c4162a") return ("err", "var(--hmd-err)");
        return ("accent", "var(--hmd-accent)");
    }

    private static double MaxForGauge(GrafanaFieldConfig? cfg, double value)
    {
        var steps = cfg?.Thresholds;
        if (steps is not null)
        {
            double hi = 0;
            foreach (var s in steps) if (s.Value is not null && s.Value.Value > hi) hi = s.Value.Value;
            if (hi > 0) return hi * 1.25;
        }
        if (value > 0) return value * 1.25;
        return 1;
    }

    private static (Dictionary<string, string> rename, HashSet<string> exclude) ParseOrganize(
        IReadOnlyList<GrafanaTransformation>? transforms)
    {
        var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (transforms is null) return (rename, exclude);
        foreach (var t in transforms)
        {
            if (t.Id != "organize" || t.Options is null) continue;
            if (t.Options.TryGetValue("excludeByName", out var ex) && ex.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in ex.EnumerateObject())
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.True) exclude.Add(p.Name);
            if (t.Options.TryGetValue("renameByName", out var rn) && rn.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in rn.EnumerateObject())
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        rename[p.Name] = p.Value.GetString() ?? p.Name;
        }
        return (rename, exclude);
    }
}