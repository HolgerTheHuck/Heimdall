using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Heimdall;
using Heimdall.Blazor.Grafana;
using Heimdall.Prometheus;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer den <see cref="GrafanaPanelRenderer"/>: jedes Panel-Kind wird
/// gegen eine In-Memory-<c>IHeimdallMetricSource</c> ausgewertet und das
/// Render-Resultat geprueft (Timeseries ms→ns, Stat-Tone, Table-organize,
/// Gauge/BarGauge/Pie, Error/Empty). Renderer wirft nie.
/// </summary>
public class GrafanaPanelRendererTests
{
    private const long S = 1_000_000_000L;   // 1 Sekunde in ns
    private const int ToMs = 3_000;         // 3 s in ms (Eval-Zeitpunkt)

    private sealed class FakeSource : IHeimdallMetricSource
    {
        public readonly List<HMetricPointView> Points = new();

        public IReadOnlyList<string> ListMetricNames(long? f = null, long? t = null)
            => Points.Select(p => p.Name).Distinct().OrderBy(n => n).ToArray();
        public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => Points.SelectMany(p => p.Labels.Keys).Distinct().OrderBy(k => k).ToArray();
        public IReadOnlyList<string> ListLabelValues(string n, IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => Points.Where(p => p.Labels.ContainsKey(n)).Select(p => p.Labels[n]).Distinct().OrderBy(v => v).ToArray();
        public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery q)
        {
            var names = q.Names is null ? null : new HashSet<string>(q.Names, StringComparer.Ordinal);
            return Points.Where(p =>
                (names is null || names.Contains(p.Name)) &&
                (!q.FromUnixNano.HasValue || p.TimeUnixNano >= q.FromUnixNano.Value) &&
                (!q.ToUnixNano.HasValue || p.TimeUnixNano <= q.ToUnixNano.Value))
                .OrderBy(p => p.Name).ThenBy(p => p.TimeUnixNano).Take(q.Limit).ToArray();
        }
    }

    private static FakeSource TwoRegionSource()
    {
        var src = new FakeSource();
        int[] eu = { 10, 21, 33, 46 }, us = { 5, 11, 17, 23 };
        for (int i = 0; i < 4; i++)
        {
            src.Points.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
                i * S, eu[i], null, null, null, null, null, null,
                new Dictionary<string, string> { ["service.name"] = "shop", ["region"] = "eu" }, "api"));
            src.Points.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
                i * S, us[i], null, null, null, null, null, null,
                new Dictionary<string, string> { ["service.name"] = "shop", ["region"] = "us" }, "api"));
        }
        return src;
    }

    /// <summary>Histogramm-Source: <c>http.server.request.duration</c> mit 3
    /// finite Bounds (0.005/0.01/0.025 s) + +Inf, 4 Punkte (t=0..3 s) mit
    /// wachsenden kumulierten Bucket-Counts, sodass <c>rate()</c> ≠ 0 liefert.</summary>
    private static FakeSource HistSource()
    {
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "shop" };
        double[] bounds = { 0.005, 0.01, 0.025 };
        long[][] countsAt = {
            new long[] { 0, 0, 0, 0 },
            new long[] { 2, 5, 8, 10 },
            new long[] { 4, 10, 16, 20 },
            new long[] { 6, 15, 24, 30 },
        };
        for (int i = 0; i < 4; i++)
        {
            var counts = countsAt[i];
            double sum = 0.01 * counts[3];   // beliebig >0, für Quantil irrelevant
            src.Points.Add(new HMetricPointView("http.server.request.duration", "s", HMetricType.Histogram,
                HTemporality.Cumulative, i * S, sum, counts[3], sum, 0, 0.025, counts, bounds, labels, "api"));
        }
        return src;
    }

    private static GrafanaPanel Panel(string type, string expr, string? legend = null,
        string? unit = null, IReadOnlyList<GrafanaThresholdStep>? thresholds = null,
        bool instant = false, IReadOnlyList<GrafanaTransformation>? transforms = null)
        => new(1, "P", type, GrafanaGridPos.Zero,
            new[] { new GrafanaTarget(expr, legend, "A", instant, instant ? "table" : null) },
            new GrafanaFieldConfig(unit, thresholds), transforms);

    private static GrafanaThresholdStep Step(double? v, string c) => new(v, c);

    // === Timeseries ========================================================
    [Fact]
    public void Timeseries_LiefertChartResult_MsZuNs_UndLegendFormat()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("timeseries", "sum by (region) (rate(orders_total[2m]))", legend: "{{region}}", unit: "reqps");
        var rp = GrafanaPanelRenderer.Render(p, eng, fromMs: 0, toMs: ToMs, stepMs: 1_000, vars: null!);
        Assert.IsType<ChartResult>(rp.Result);
        var cr = (ChartResult)rp.Result;
        Assert.Equal("reqps", cr.Unit);
        Assert.Equal(2, cr.Series.Count);
        var labels = cr.Series.Select(s => s.Label).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "eu", "us" }, labels);
        // ms → ns: jeder Zeitstempel durch 1_000_000 teilbar (kam von ms-int);
        // der letzte Punkt liegt bei ToMs (3 s) → 3_000_000_000 ns.
        foreach (var s in cr.Series)
        {
            Assert.NotEmpty(s.Points);
            Assert.All(s.Points, p => Assert.True(p.T % 1_000_000 == 0));
            Assert.Equal(ToMs * 1_000_000L, s.Points[^1].T);
        }
    }

    [Fact]
    public void Timeseries_OhneLegendFormat_NimmtFingerprint()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("timeseries", "sum by (region) (rate(orders_total[2m]))");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var cr = Assert.IsType<ChartResult>(rp.Result);
        Assert.All(cr.Series, s => Assert.Contains("region=", s.Label)); // Fingerprint
    }

    // === Stat ==============================================================
    [Fact]
    public void Stat_LiefertWertUndToneAusThresholds()
    {
        var eng = new PromEngine(TwoRegionSource());
        // sum(orders_total) = 46 + 23 = 69 → gelb (>=50, <200).
        var p = Panel("stat", "sum(orders_total)", unit: "reqps",
            thresholds: new[] { Step(null, "green"), Step(50, "yellow"), Step(200, "red") });
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var st = Assert.IsType<StatResult>(rp.Result);
        Assert.Equal(69, st.Value, 5);
        Assert.Equal("warn", st.Tone);
        Assert.Equal("reqps", st.Unit);
    }

    [Fact]
    public void Stat_Leer_LiefertEmpty()
    {
        var eng = new PromEngine(new FakeSource());
        var p = Panel("stat", "sum(orders_total)");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        Assert.IsType<EmptyResult>(rp.Result);
    }

    [Fact]
    public void Stat_MultiSerie_LiefertKachelProSerie_MitSparklinePunkten()
    {
        // sum by (region)(orders_total) → 2 Serien (eu=46, us=23) → StatGridResult.
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("stat", "sum by (region) (orders_total)", unit: "reqps");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var grid = Assert.IsType<StatGridResult>(rp.Result);
        Assert.Equal(2, grid.Tiles.Count);
        // Sortiert nach RawValue desc → eu(46) vor us(23).
        Assert.Equal("eu", grid.Tiles[0].Label);
        Assert.Equal(46, grid.Tiles[0].RawValue, 5);
        Assert.Equal("us", grid.Tiles[1].Label);
        Assert.Equal(23, grid.Tiles[1].RawValue, 5);
        // Sparkline-Punkte (Range über 4 Schritte → 4 Proben je Serie).
        Assert.True(grid.Tiles[0].Points.Count >= 2);
        Assert.Equal("reqps", grid.Unit);
    }

    [Fact]
    public void Stat_GraphModeArea_EineSerie_LiefertKachelMitSparkline_NichtBlosseZahl()
    {
        // Grafana stat-Panel mit graphMode=area zeigt AUCH bei EINER Serie eine
        // Kachel mit Mini-Graph (Sparkline) + letztem Wert — nicht nur die Zahl.
        // Demo-Daten haben z. B. kein http_response_status_code-Label → eine Serie.
        var eng = new PromEngine(TwoRegionSource());
        var p = new GrafanaPanel(1, "Rates", "stat", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("sum(orders_total)", null, "A", false, null) },
            new GrafanaFieldConfig("reqps", null), null, DatasourceType: "prometheus", StatGraphMode: "area");
        Assert.True(p.WantsStatGraph);
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var grid = Assert.IsType<StatGridResult>(rp.Result);   // nicht StatResult (bloße Zahl)
        Assert.Single(grid.Tiles);
        Assert.True(grid.Tiles[0].Points.Count >= 2);          // Sparkline-Daten vorhanden
        Assert.Equal("reqps", grid.Unit);
    }

    [Fact]
    public void Stat_GraphModeNone_EineSerie_BleibtGrosseZahl()
    {
        // graphMode=none/ohne → bewährter Einzelwert-Pfad (HeimdallKpi, große Zahl).
        var eng = new PromEngine(TwoRegionSource());
        var p = new GrafanaPanel(1, "Total", "stat", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("sum(orders_total)", null, "A", false, null) },
            new GrafanaFieldConfig("reqps", null), null, DatasourceType: "prometheus", StatGraphMode: "none");
        Assert.False(p.WantsStatGraph);
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        Assert.IsType<StatResult>(rp.Result);
    }

    // === Table =============================================================
    [Fact]
    public void Table_LiefertSpaltenUndZeilen_MitOrganize()
    {
        var eng = new PromEngine(TwoRegionSource());
        var opts = new Dictionary<string, JsonElement>();
        using (var doc = JsonDocument.Parse("""{"excludeByName":{"Time":true},"renameByName":{"Value":"req/s"}}"""))
            foreach (var kp in doc.RootElement.EnumerateObject()) opts[kp.Name] = kp.Value.Clone();
        var transforms = new[] { new GrafanaTransformation("organize", opts) };

        var p = Panel("table", "sum by (region) (orders_total)", instant: true, transforms: transforms);
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var tb = Assert.IsType<TableResult>(rp.Result);
        // Spalten: region (unbenannt), Value → "req/s". Time excludiert (nicht vorhanden).
        Assert.Equal(new[] { "region", "req/s" }, tb.Columns.ToArray());
        Assert.Equal(2, tb.Rows.Count);
        var first = tb.Rows.OrderBy(r => r[0]).First();
        Assert.Equal("eu", first[0]);
        Assert.Equal("46", first[1]);   // FmtValue(46) = "46"
    }

    // === Gauge =============================================================
    [Fact]
    public void Gauge_LiefertWertMinMaxUndFarbe()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("gauge", "sum(orders_total)", unit: "reqps",
            thresholds: new[] { Step(null, "green"), Step(50, "yellow"), Step(200, "red") });
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var g = Assert.IsType<GaugeResult>(rp.Result);
        Assert.Equal(69, g.Value, 5);
        Assert.Equal(0, g.Min);
        Assert.Equal(200 * 1.25, g.Max, 2);   // hoechster Threshold * 1,25
        Assert.Equal("warn", g.Tone);
        Assert.Equal("var(--hmd-warn)", g.Color);
    }

    // === BarGauge ==========================================================
    [Fact]
    public void BarGauge_LiefertProSerieEinenBalken()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("bargauge", "sum by (region) (orders_total)", legend: "{{region}}");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var bg = Assert.IsType<BarGaugeResult>(rp.Result);
        var rows = bg.Rows.OrderBy(r => r.Label).ToArray();
        Assert.Equal(new[] { "eu", "us" }, rows.Select(r => r.Label).ToArray());
        Assert.Equal(46, rows[0].Value, 5);
        Assert.Equal(23, rows[1].Value, 5);
        Assert.Equal(46, rows[0].Max, 5);   // Max = Maximum aller Serien
    }

    // === Pie ===============================================================
    [Fact]
    public void Pie_LiefertSlicesProSerie()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("pie", "sum by (region) (orders_total)", legend: "{{region}}");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var pie = Assert.IsType<PieResult>(rp.Result);
        var slices = pie.Slices.OrderBy(s => s.Label).ToArray();
        Assert.Equal(new[] { "eu", "us" }, slices.Select(s => s.Label).ToArray());
        Assert.Equal(46, slices[0].Value, 5);
    }

    // === Heatmap (Zeit × Histogramm-Bucket) =================================
    [Fact]
    public void Heatmap_LiefertBucketsAufsteigend_MitPlusInfZuletzt()
    {
        var eng = new PromEngine(HistSource());
        var p = Panel("heatmap", "sum(rate(http_server_request_duration_seconds_bucket[2m])) by (le)");
        var rp = GrafanaPanelRenderer.Render(p, eng, fromMs: 0, toMs: ToMs, stepMs: 1_000, vars: null!);
        var hm = Assert.IsType<HeatmapResult>(rp.Result);
        // 3 finite Bounds + +Inf → 4 le-Buckets, aufsteigend, +Inf zuletzt.
        Assert.Equal(4, hm.Buckets.Count);
        Assert.True(hm.Buckets[0].UpperBound < hm.Buckets[1].UpperBound);
        Assert.True(hm.Buckets[1].UpperBound < hm.Buckets[2].UpperBound);
        Assert.True(double.IsPositiveInfinity(hm.Buckets[^1].UpperBound));
        Assert.Equal("∞", hm.Buckets[^1].Label);
        // Inkrementelle Rate > 0 (Counts wachsen über die 4 Punkte).
        Assert.True(hm.MaxValue > 0, "MaxValue=" + hm.MaxValue.ToString(CultureInfo.InvariantCulture));
        Assert.NotEmpty(hm.ColumnTimesMs);
    }

    [Fact]
    public void Heatmap_OhneLeLabel_LiefertEmpty()
    {
        // orders_total trägt kein le-Label → keine Histogramm-Buckets → Empty.
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("heatmap", "sum by (region) (rate(orders_total[2m]))");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        Assert.IsType<EmptyResult>(rp.Result);
    }

    // === Error / Unknown ===================================================
    [Fact]
    public void BoesesPromQL_LiefertErrorResult_NichtGeworfen()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("stat", "rate(");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var err = Assert.IsType<ErrorResult>(rp.Result);
        Assert.False(string.IsNullOrEmpty(err.Message));
    }

    [Fact]
    public void PanelOhneTargets_LiefertEmpty()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = new GrafanaPanel(1, "P", "stat", GrafanaGridPos.Zero,
            Array.Empty<GrafanaTarget>(), null, null);
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        Assert.IsType<EmptyResult>(rp.Result);
    }

    [Fact]
    public void RowPanel_LiefertRowResult()
    {
        // Row-Section-Header haben keine Targets/keine Auswertung → RowResult
        // (UI rendert Vollbreite-Überschrift, nicht „Keine PromQL-Targets").
        var eng = new PromEngine(TwoRegionSource());
        var p = new GrafanaPanel(1, "RED", "row", GrafanaGridPos.Zero,
            Array.Empty<GrafanaTarget>(), null, null);
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        Assert.IsType<RowResult>(rp.Result);
    }

    [Fact]
    public void NichtPrometheusDatasource_NichtLogs_LiefertErrorStattParseCrash()
    {
        // Ein Stat-Panel (nicht-Logs-Typ) mit Loki-Datasource kann Heimdall nicht
        // auswerten → klarer Hinweis statt PromQL-Parse-Crash. (Logs-Panels werden
        // eigene ausgewertet — siehe LogsPanel_*-Tests.)
        var eng = new PromEngine(TwoRegionSource());
        var p = new GrafanaPanel(1, "X", "stat", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("{service_name=\"shop\"} |= ``", null, "A", false, null) },
            null, null, null, "loki");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        var err = Assert.IsType<ErrorResult>(rp.Result);
        Assert.Contains("loki", err.Message);
        Assert.Contains("PromQL", err.Message);
    }

    // === Logs (Loki → Heimdall-Log-Store) =================================

    /// <summary>Fake-Log-Store: filtert wie die echte Impl (Text/Sev/Zeit).</summary>
    private sealed class FakeLogQuery : IHeimdallQuery
    {
        public readonly List<LogRow> Logs = new();
        public IReadOnlyList<LogRow> SearchLogs(LogSearch s)
        {
            var q = Logs.AsEnumerable();
            if (!string.IsNullOrEmpty(s.Text))
                q = q.Where(l => (l.Body ?? string.Empty).Contains(s.Text, StringComparison.OrdinalIgnoreCase));
            if (s.MinSeverity.HasValue) q = q.Where(l => l.Severity >= s.MinSeverity.Value);
            if (s.FromUnixNano.HasValue) q = q.Where(l => l.TimeUnixNano >= s.FromUnixNano.Value);
            if (s.ToUnixNano.HasValue) q = q.Where(l => l.TimeUnixNano <= s.ToUnixNano.Value);
            return q.Take(s.Limit < 1 ? 200 : s.Limit).ToList();
        }
        public IReadOnlyList<TraceSummary> ListTraces(TraceFilter f) => Array.Empty<TraceSummary>();
        public IReadOnlyList<SpanRow> GetTrace(string t) => Array.Empty<SpanRow>();
        public IReadOnlyList<SpanRow> ListSpans(SpanFilter f) => Array.Empty<SpanRow>();
        public IReadOnlyList<MetricRow> MetricSeries(string n, long? f, long? t, int lim = 500) => Array.Empty<MetricRow>();
        public long CountSpans() => 0;
        public long CountLogs() => Logs.Count;
        public long CountMetrics() => 0;
    }

    private static LogRow Row(long ns, int sev, string body, string? attrs = null) =>
        new(ns, null, null, sev, null, body, attrs ?? "{}", "api");

    [Fact]
    public void LogsPanel_LiefertLogResultAusEigenemStore()
    {
        // Loki-Logs-Panel wird gegen Heimdalls eigenen Log-Store ausgewertet —
        // früher: „Datenquelle 'loki' wird nicht unterstützt". Jetzt: LogResult.
        var eng = new PromEngine(TwoRegionSource());
        var q = new FakeLogQuery();
        q.Logs.Add(Row(1_000_000_000L, 9, "order placed for alice"));
        q.Logs.Add(Row(2_000_000_000L, 17, "db timeout in query"));
        var p = new GrafanaPanel(1, "Logs", "logs", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("{service_name=\"shop\"} |= \"\"", null, "A", false, null) },
            null, null, null, "loki");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!, q);
        var logs = Assert.IsType<LogResult>(rp.Result);
        Assert.Equal(2, logs.Rows.Count);
        Assert.Equal(0, logs.TruncatedCount);
    }

    [Fact]
    public void LogsPanel_Zeilenfilter_FiltertInMemoryCaseSensitive()
    {
        // |= ist case-sensitive (Loki-Semantik): „timeout" trifft nicht „TIMEOUT".
        var eng = new PromEngine(TwoRegionSource());
        var q = new FakeLogQuery();
        q.Logs.Add(Row(1_000_000_000L, 9, "order placed for alice"));
        q.Logs.Add(Row(2_000_000_000L, 17, "db timeout in query"));
        q.Logs.Add(Row(3_000_000_000L, 17, "TIMEOUT elsewhere"));
        var p = new GrafanaPanel(1, "Logs", "logs", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("{service_name=\"shop\"} |= \"timeout\"", null, "A", false, null) },
            null, null, null, "loki");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!, q);
        var logs = Assert.IsType<LogResult>(rp.Result);
        Assert.Single(logs.Rows);
        Assert.Equal("db timeout in query", logs.Rows[0].Body);
    }

    [Fact]
    public void LogsPanel_RegexFilter_Negativfilter_Kombinieren()
    {
        // |~ "db.*timeout" trifft „db timeout"; != „alice" schließt nichts davon aus.
        var eng = new PromEngine(TwoRegionSource());
        var q = new FakeLogQuery();
        q.Logs.Add(Row(1_000_000_000L, 9, "order placed for alice"));
        q.Logs.Add(Row(2_000_000_000L, 17, "db timeout in query"));
        var p = new GrafanaPanel(1, "Logs", "logs", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("{service_name=\"shop\"} |~ \"db.*timeout\" != \"alice\"", null, "A", false, null) },
            null, null, null, "loki");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!, q);
        var logs = Assert.IsType<LogResult>(rp.Result);
        Assert.Single(logs.Rows);
        Assert.Contains("timeout", logs.Rows[0].Body!);
    }

    [Fact]
    public void LogsPanel_Leer_LiefertEmpty()
    {
        var eng = new PromEngine(TwoRegionSource());
        var q = new FakeLogQuery();   // keine Logs
        var p = new GrafanaPanel(1, "Logs", "logs", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("{service_name=\"shop\"}", null, "A", false, null) },
            null, null, null, "loki");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!, q);
        Assert.IsType<EmptyResult>(rp.Result);
    }

    [Fact]
    public void LogsPanel_OhneQuery_LiefertError()
    {
        // Embedded ohne IHeimdallQuery: Logs-Panel kann nicht ausgewertet werden →
        // Hinweis statt Crash (PromQL-Panels laufen weiter, query ist optional).
        var eng = new PromEngine(TwoRegionSource());
        var p = new GrafanaPanel(1, "Logs", "logs", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("{service_name=\"shop\"}", null, "A", false, null) },
            null, null, null, "loki");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);  // query = null (Default)
        Assert.IsType<ErrorResult>(rp.Result);
    }

    [Fact]
    public void LogsPanel_LabelMatcher_FiltertNachAttrNormalisiert()
    {
        // Loki service_name ↔ OTel service.name (_/.-Normalisierung); fehlt das
        // Label, schließt es die Zeile nicht aus (Best-Effort, flacher Store).
        var eng = new PromEngine(TwoRegionSource());
        var q = new FakeLogQuery();
        q.Logs.Add(Row(1_000_000_000L, 9, "ok", "{\"service.name\":\"shop\"}"));
        q.Logs.Add(Row(2_000_000_000L, 17, "err", "{\"service.name\":\"billing\"}"));
        q.Logs.Add(Row(3_000_000_000L, 9, "noattr", "{}"));
        var p = new GrafanaPanel(1, "Logs", "logs", GrafanaGridPos.Zero,
            new[] { new GrafanaTarget("{service_name=\"shop\"}", null, "A", false, null) },
            null, null, null, "loki");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!, q);
        var logs = Assert.IsType<LogResult>(rp.Result);
        // „shop" trifft (normalisiert), „billing" nicht, „noattr" best-effort durch.
        Assert.Equal(2, logs.Rows.Count);
        Assert.Contains("ok", logs.Rows.Select(r => r.Body));
        Assert.Contains("noattr", logs.Rows.Select(r => r.Body));
    }

    [Fact]
    public void UnbekannterTyp_FaelltAufTimeseriesZurueck()
    {
        var eng = new PromEngine(TwoRegionSource());
        var p = Panel("alertlist", "sum by (region) (rate(orders_total[2m]))", legend: "{{region}}");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, null!);
        Assert.IsType<ChartResult>(rp.Result);
    }

    // === Templating-Interpolation im Renderer ==============================
    [Fact]
    public void Render_InterpoliertVarVorEval()
    {
        var eng = new PromEngine(TwoRegionSource());
        var vars = new Dictionary<string, string> { ["region"] = "eu" };
        var p = Panel("stat", "sum(orders_total{region=~\"$region\"})");
        var rp = GrafanaPanelRenderer.Render(p, eng, 0, ToMs, 1_000, vars);
        var st = Assert.IsType<StatResult>(rp.Result);
        Assert.Equal(46, st.Value, 5);     // nur eu → 46
    }
}