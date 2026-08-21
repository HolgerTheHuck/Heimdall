using System.Collections.Generic;
using Heimdall.Blazor;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Smoke-Tests fuer die server-seitigen SVG-Renderer (Gauge/BarGauge/Pie) in
/// <see cref="HeimdallCharting"/>: nicht-leer, wohlgeformter SVG-Rahmen,
/// Attribut-Klassen und Absicherung gegen Entartungen (max&lt;=min, leer).
/// </summary>
public class GrafanaSvgRendererTests
{
    [Fact]
    public void GaugeSvg_LiefertValidesSvgMitKlassen()
    {
        var svg = HeimdallCharting.RenderGaugeSvg(50, 0, 100, "var(--hmd-ok)", "RPS", 300, 160);
        Assert.Contains("<svg", svg);
        Assert.Contains("hmd-gauge", svg);
        Assert.Contains("hmd-gauge-track", svg);
        Assert.Contains("hmd-gauge-val", svg);
        Assert.Contains(">50<", svg);                 // Wertmittig
        Assert.Contains("stroke=\"var(--hmd-ok)\"", svg);
        Assert.EndsWith("</svg>", svg.TrimEnd());
    }

    [Fact]
    public void GaugeSvg_Entartet_MinGleichMax_KeineNaN()
    {
        var svg = HeimdallCharting.RenderGaugeSvg(double.NaN, 50, 50, "red", "X", 300, 160);
        Assert.Contains("<svg", svg);                   // wirft nicht, kein NaN im Text
        Assert.DoesNotContain("NaN", svg);
    }

    [Fact]
    public void BarGaugeSvg_LiefertBalkenProZeile()
    {
        var rows = new List<BarGaugeRow>
        {
            new("/cart", 46, 46, "var(--hmd-ok)", null),
            new("/api/orders", 23, 46, "var(--hmd-accent)", null),
        };
        var svg = HeimdallCharting.RenderBarGaugeSvg(rows, 760, 100);
        Assert.Contains("<svg", svg);
        Assert.Contains("hmd-bargauge", svg);
        Assert.Contains("hmd-bg-track", svg);
        Assert.Contains("hmd-bg-fill", svg);
        Assert.Contains("/cart", svg);
        Assert.Contains("/api/orders", svg);
    }

    [Fact]
    public void BarGaugeSvg_Leer_LiefertLeerenString()
    {
        Assert.Equal(string.Empty, HeimdallCharting.RenderBarGaugeSvg(null!, 760, 100));
        Assert.Equal(string.Empty, HeimdallCharting.RenderBarGaugeSvg(new List<BarGaugeRow>(), 760, 100));
    }

    [Fact]
    public void PieSvg_LiefertSlicesUndLegende()
    {
        var slices = new List<PieSlice>
        {
            new("eu", 46, "var(--hmd-accent)"),
            new("us", 23, "var(--hmd-ok)"),
        };
        var svg = HeimdallCharting.RenderPieSvg(slices, 360, 220);
        Assert.Contains("<svg", svg);
        Assert.Contains("hmd-pie", svg);
        Assert.Contains("hmd-pie-slice", svg);
        Assert.Contains("eu", svg);
        Assert.Contains("us", svg);
    }

    [Fact]
    public void PieSvg_SummeNull_LiefertLeerkreis()
    {
        var slices = new List<PieSlice> { new("a", 0, "red"), new("b", 0, "blue") };
        var svg = HeimdallCharting.RenderPieSvg(slices, 360, 220);
        Assert.Contains("hmd-pie-empty", svg);
    }

    // === Heatmap (Zeit × Histogramm-Bucket) =================================

    [Fact]
    public void HeatmapSvg_LiefertZellenProWert_UeberspringtNullzellen()
    {
        var buckets = new List<HeatmapBucket>
        {
            new(0.005, "5 ms", new double[] { 0, 1, 2 }),
            new(0.01,  "10 ms", new double[] { 2, 0, 3 }),
            new(double.PositiveInfinity, "∞", new double[] { 1, 1, 0 }),
        };
        var times = new long[] { 0, 60_000, 120_000 };
        var svg = HeimdallCharting.RenderHeatmapSvg(buckets, times, maxValue: 3, width: 400, height: 200);
        Assert.Contains("<svg", svg);
        Assert.Contains("hmd-heatmap", svg);
        Assert.Contains("hmd-heat-cell", svg);
        Assert.Contains("5 ms", svg);
        Assert.Contains("∞", svg);
        // 3×3 = 9 Zellen, davon 3 Null (≤1e-9) → 6 rects gerendert.
        Assert.Equal(6, CountOccurrences(svg, "hmd-heat-cell"));
        Assert.EndsWith("</svg>", svg.TrimEnd());
    }

    [Fact]
    public void HeatmapSvg_Leer_LiefertLeerenString()
    {
        Assert.Equal(string.Empty, HeimdallCharting.RenderHeatmapSvg(null!, new long[] { 0 }, 1, 400, 200));
        Assert.Equal(string.Empty, HeimdallCharting.RenderHeatmapSvg(new List<HeatmapBucket>(), new long[] { 0 }, 1, 400, 200));
        Assert.Equal(string.Empty, HeimdallCharting.RenderHeatmapSvg(
            new List<HeatmapBucket> { new(1, "1 s", new double[] { 1 }) }, new long[0], 1, 400, 200));
    }

    // === Stat-Sparkline (Mini-Flächen-Graph pro Kachel) ====================

    [Fact]
    public void StatSparklineSvg_LiefertFlaecheMitPunkten()
    {
        var pts = new List<(long Tms, double V)> { (0, 10), (1000, 21), (2000, 33), (3000, 46) };
        var svg = HeimdallCharting.RenderStatSparklineSvg(pts, "ok", 120, 30);
        Assert.Contains("<svg", svg);
        Assert.Contains("hmd-stat-spark", svg);
        Assert.Contains("hmd-stat-spark-area", svg);
        Assert.Contains("hmd-stat-spark-line", svg);
        Assert.Contains("hmd-spark--ok", svg);
        Assert.EndsWith("</svg>", svg.TrimEnd());
    }

    [Fact]
    public void StatSparklineSvg_ZuWenigePunkte_LiefertLeer()
    {
        Assert.Equal(string.Empty, HeimdallCharting.RenderStatSparklineSvg(null!, "ok", 120, 30));
        Assert.Equal(string.Empty,
            HeimdallCharting.RenderStatSparklineSvg(new List<(long, double)> { (0, 10) }, "ok", 120, 30));
    }

    private static int CountOccurrences(string s, string sub)
    {
        int c = 0, i = 0;
        while ((i = s.IndexOf(sub, i, System.StringComparison.Ordinal)) >= 0) { c++; i += sub.Length; }
        return c;
    }

    // === Regression: PromQL +Inf/NaN-Proben dürfen Chart-Rendering nicht crashen ===

    /// <summary>
    /// Regression für den JsonSerializer-Crash (ArgumentException „positive and negative
    /// infinity cannot be written as valid JSON") beim Rendern des heimdall-overview-
    /// Dashboards: eine PromQL-Serie kann +Inf/NaN-Proben enthalten (Division durch 0,
    /// rate über leerem Fenster, histogram_quantile ohne Buckets). ScaleChart muss diese
    /// überspringen (Lücke statt Punkt), sodass weder die SVG-Koordinaten noch der
    /// JSON-Payload (hmd-chart-data) nicht-finite Werte sehen — und die endlichen Proben
    /// bleiben gerendert.
    /// </summary>
    [Fact]
    public void LineSvg_SerieMitInfinityUndNaN_UeberspringtNichtFiniteProben()
    {
        // t0=finite, t1=+Inf, t2=NaN, t3=finite → nur t0 und t3 dürfen gerendert werden.
        var pts = new List<(long T, double V)>
        {
            (1_000_000_000L, 10.0),
            (2_000_000_000L, double.PositiveInfinity),
            (3_000_000_000L, double.NaN),
            (4_000_000_000L, 40.0),
        };
        var series = new List<ChartSeries> { new("orders", "var(--hmd-accent)", pts) };

        var geo = HeimdallCharting.ScaleChart(series, 600, 200);
        Assert.NotNull(geo);
        var svg = HeimdallCharting.RenderLineSvg(geo!, "orders");

        // Kein Crash, valides SVG, kein „NaN"/„Infinity" in Koordinaten oder JSON-Payload.
        Assert.Contains("<svg", svg);
        Assert.DoesNotContain("NaN", svg);
        Assert.DoesNotContain("Infinity", svg);
        // JSON-Payload (pts=[x,y,t,v]-Arrays) vorhanden; endliche Proben t0/v=10 und
        // t3/v=40 gerendert, nicht-finite Proben t1(+Inf)/t2(NaN) übersprungen.
        Assert.Contains("hmd-chart-data", svg);
        Assert.Contains("1000000000,10", svg);    // t0=1e9, v=10 (t,v-Paar im pts-Array)
        Assert.Contains("4000000000,40", svg);    // t3=4e9, v=40
        Assert.DoesNotContain("2000000000", svg); // t1 mit +Inf übersprungen
        Assert.DoesNotContain("3000000000", svg); // t2 mit NaN übersprungen
    }

    /// <summary>
    /// Eine Serie, die NUR aus +Inf/NaN besteht, liefert kein Chart (null) statt eines
    /// Crashes — graceful Empty statt Exception.
    /// </summary>
    [Fact]
    public void ScaleChart_SerieNurAusInfinity_LiefertNull()
    {
        var pts = new List<(long T, double V)>
        {
            (1_000_000_000L, double.PositiveInfinity),
            (2_000_000_000L, double.NaN),
            (3_000_000_000L, double.NegativeInfinity),
        };
        var series = new List<ChartSeries> { new("x", "red", pts) };
        Assert.Null(HeimdallCharting.ScaleChart(series, 600, 200));
    }
}