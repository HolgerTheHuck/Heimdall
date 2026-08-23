using System;
using Heimdall.Blazor;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Reine Helfer-Tests fuer die Dashboard-UI (HeimdallCharting). Deckt die JSON-Parser
/// (Attribute/Buckets) und die entartete Eingabe der Chart-Mathematik ab — kein
/// Razor-Render, nur die internen, werferfreien Funktionen.
/// </summary>
public class HeimdallBlazorUiTests
{
    // --- ParseAttrs --------------------------------------------------------

    [Fact]
    public void ParseAttrs_FlatObject_Empty_Malformed()
    {
        var a = HeimdallCharting.ParseAttrs("{\"a\":1,\"b\":\"x\",\"c\":true,\"d\":null}");
        Assert.Equal(3, a.Count);   // null-Wert wird verworfen
        Assert.Equal("a", a[0].Key); Assert.Equal("1", a[0].Value);
        Assert.Equal("b", a[1].Key); Assert.Equal("x", a[1].Value);
        Assert.Equal("c", a[2].Key); Assert.Equal("true", a[2].Value);

        Assert.Empty(HeimdallCharting.ParseAttrs("{}"));
        Assert.Empty(HeimdallCharting.ParseAttrs(null));
        Assert.Empty(HeimdallCharting.ParseAttrs(""));
        Assert.Empty(HeimdallCharting.ParseAttrs("q{ kaputtes json"));  // kein Throw
        Assert.Empty(HeimdallCharting.ParseAttrs("[1,2,3]"));            // Array, kein Object
    }

    [Fact]
    public void FormatAttrsLabel_Compact()
    {
        var a = HeimdallCharting.ParseAttrs("{\"region\":\"eu\",\"http.method\":\"GET\"}");
        Assert.Equal("region=eu http.method=GET", HeimdallCharting.FormatAttrsLabel(a));
        Assert.Equal("(keine Attribute)", HeimdallCharting.FormatAttrsLabel(HeimdallCharting.ParseAttrs("{}")));
    }

    // --- ParseLongs / ParseDoubles (Histogrammbuckets) ---------------------

    [Fact]
    public void ParseLongs_ParseDoubles_HistogramBuckets()
    {
        Assert.Empty(HeimdallCharting.ParseLongs("[]"));
        Assert.Empty(HeimdallCharting.ParseLongs(null));
        var ls = HeimdallCharting.ParseLongs("[1,2,3]");
        Assert.Equal(new long[] { 1, 2, 3 }, ls);

        Assert.Empty(HeimdallCharting.ParseDoubles("[]"));
        var ds = HeimdallCharting.ParseDoubles("[0.5,1.5,2.5]");
        Assert.Equal(new double[] { 0.5, 1.5, 2.5 }, ds);

        Assert.Empty(HeimdallCharting.ParseLongs("kein array"));  // kein Throw
    }

    // --- ScaleChart: entartete Eingaben ------------------------------------

    [Fact]
    public void ScaleChart_Empty_ReturnsNull()
    {
        Assert.Null(HeimdallCharting.ScaleChart(Array.Empty<ChartSeries>(), 800, 240));
        Assert.Null(HeimdallCharting.ScaleChart(null!, 800, 240));
    }

    [Fact]
    public void ScaleChart_SinglePoint_And_Constant_NotDivByZero()
    {
        var one = new[] {
            new ChartSeries("s", "red", new[] { (100L, 5.0) })
        };
        var g = HeimdallCharting.ScaleChart(one, 800, 240);
        Assert.NotNull(g);
        Assert.True(g!.Series.Count == 1);
        Assert.False(string.IsNullOrEmpty(g.Series[0].Points));
        // 5 y-Gridlines + 6 x-Ticks, alle endlich.
        Assert.Equal(5, g.YGrid.Count);
        Assert.Equal(6, g.XTicks.Count);
        foreach (var yl in g.YGrid) Assert.False(double.IsNaN(yl.Y));

        var constant = new[] {
            new ChartSeries("s", "red",
                new[] { (100L, 7.0), (200L, 7.0), (300L, 7.0) })
        };
        var g2 = HeimdallCharting.ScaleChart(constant, 800, 240);
        Assert.NotNull(g2);
        Assert.Equal(6, g2!.XTicks.Count);
        foreach (var yl in g2.YGrid) Assert.False(double.IsNaN(yl.Y));
    }

    [Fact]
    public void ScaleChart_MultiSeries_MapsIntoPlot()
    {
        var series = new[] {
            new ChartSeries("a", "var(--hmd-accent)",
                new[] { (0L, 0.0), (10L, 10.0) }),
            new ChartSeries("b", "var(--hmd-ok)",
                new[] { (0L, 2.0), (10L, 8.0) }),
        };
        var g = HeimdallCharting.ScaleChart(series, 800, 240);
        Assert.NotNull(g);
        Assert.Equal(2, g!.Series.Count);
        // Alle Punkte liegen innerhalb des Plot-Bereichs (mit Padding).
        foreach (var s in g.Series)
        {
            foreach (var tok in s.Points.Split(' '))
            {
                var c = tok.Split(',');
                double x = double.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture);
                double y = double.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(x, g.PadLeft - 0.5, g.PadLeft + g.PlotW + 0.5);
                Assert.InRange(y, g.PadTop - 0.5, g.PadTop + g.PlotH + 0.5);
            }
        }
    }
}