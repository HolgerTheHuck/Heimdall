using System;
using System.Linq;
using Heimdall;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Layout-Helfer des kombinierten Span-Zeitstrahls (HeimdallTraceWaterfall): Order
/// (DFS-Preorder + Tiefe, Zyklen-/Waisen-Fallback), TraceRange, Histogram und BarStyle
/// (invariant formatierte %-Positionierung). Plain xUnit ohne Host — IVT macht die
/// interne Klasse auf allen TFMs sichtbar.
/// </summary>
public class HeimdallTraceWaterfallTests
{
    private static SpanRow Span(string traceId, string spanId, string? parent, string name,
        long start, long end, int kind = (int)HSpanKind.Server, int status = (int)HStatusCode.Ok)
        => new(traceId, spanId, parent ?? "", name, kind, start, end, end - start, status,
            null, "{}", "[]", "{}", null);

    [Fact]
    public void Order_DfsPreorderMitTiefe_ParentVorChild()
    {
        // Absichtlich unsortiert + Child vor Parent im Eingabe-Array (byId-Map muss lösen).
        var spans = new[]
        {
            Span("t", "c2", "root", "child-b", 1_200, 1_400, (int)HSpanKind.Client),
            Span("t", "root", null, "GET /", 1_000, 1_500),
            Span("t", "c1", "root", "child-a", 1_100, 1_300, (int)HSpanKind.Internal),
        };

        var ordered = Heimdall.Blazor.HeimdallTraceWaterfall.Order(spans);

        Assert.Equal(new[] { "root", "c1", "c2" }, ordered.Select(o => o.Span.SpanId).ToArray());
        Assert.Equal(new[] { 0, 1, 1 }, ordered.Select(o => o.Depth).ToArray());
    }

    [Fact]
    public void Order_Zyklus_KeinThrow_AlleSpansErreicht()
    {
        var spans = new[] { Span("t", "a", "b", "A", 0, 10), Span("t", "b", "a", "B", 5, 15) };

        var ordered = Heimdall.Blazor.HeimdallTraceWaterfall.Order(spans);

        Assert.Equal(spans.Length, ordered.Count);
        Assert.All(ordered, o => Assert.Equal(0, o.Depth));   // Fallback hängt bei Tiefe 0 an
    }

    [Fact]
    public void TraceRange_NullLeerUndDegeneriert_WerfenNie()
    {
        Assert.Equal(0, Heimdall.Blazor.HeimdallTraceWaterfall.TraceRange(null).StartUnixNano);
        Assert.Equal(1, Heimdall.Blazor.HeimdallTraceWaterfall.TraceRange(Array.Empty<SpanRow>()).EndUnixNano);

        // End == Start → Fallback Start+1.
        var r = Heimdall.Blazor.HeimdallTraceWaterfall.TraceRange(new[] { Span("t", "a", null, "A", 500, 500) });
        Assert.Equal(500, r.StartUnixNano);
        Assert.Equal(501, r.EndUnixNano);
    }

    [Fact]
    public void Histogram_CountsSummierenUndBucketsMonoton()
    {
        var spans = new[] { Span("t", "a", null, "A", 0, 100), Span("t", "b", null, "B", 45, 55) };
        var hist = Heimdall.Blazor.HeimdallTraceWaterfall.Histogram(spans, (0, 100L), 10);

        Assert.Equal(10, hist.Count);
        Assert.Equal(2, hist.Sum(h => h.Count));
        Assert.Equal(8, hist.Count(h => h.Count == 0));   // Null-Buckets bleiben enthalten
        for (var i = 1; i < hist.Count; i++)
            Assert.True(hist[i - 1].ToUnixNano <= hist[i].FromUnixNano);
    }

    [Fact]
    public void BarStyle_WurzelnUndPositionen_InvariantFormatiert()
    {
        // Root-Span = gesamte Trace-Spanne → Vollbreite bei left 0.
        var root = Heimdall.Blazor.HeimdallTraceWaterfall.BarStyle(Span("t", "a", null, "A", 0, 500_000_000), 0, 500_000_000);
        Assert.Equal("left:0%;width:100%;background:var(--hmd-accent)", root);

        // Child bei 50ms von 500ms → left:10%, width:30% (150ms).
        var child = Heimdall.Blazor.HeimdallTraceWaterfall.BarStyle(
            Span("t", "b", "a", "B", 50_000_000, 200_000_000, (int)HSpanKind.Client), 0, 500_000_000);
        Assert.StartsWith("left:10%;width:30%", child);
        Assert.EndsWith("background:var(--hmd-ok)", child);

        // Invarianter Dezimaltrenner: niemals ',' (bräche das CSS).
        Assert.DoesNotContain(",", root);
        Assert.DoesNotContain(",", child);
    }

    [Fact]
    public void BarStyle_FehlerSpanOverrideUndClamps()
    {
        // Fehler-Override (Kind egal).
        var err = Heimdall.Blazor.HeimdallTraceWaterfall.BarStyle(Span("t", "a", null, "A", 0, 1, status: (int)HStatusCode.Error), 0, 100);
        Assert.Contains("var(--hmd-err)", err);

        // Zero-Duration → Mindestbreite 0.5 %.
        var zero = Heimdall.Blazor.HeimdallTraceWaterfall.BarStyle(Span("t", "a", null, "A", 50, 50), 0, 100);
        Assert.StartsWith("left:50%;width:0.5%", zero);

        // rangeSpanNs ≤ 0 → Vollbreite, kein Throw.
        var full = Heimdall.Blazor.HeimdallTraceWaterfall.BarStyle(Span("t", "a", null, "A", 0, 1), 0, 0);
        Assert.Equal("left:0%;width:100%;background:var(--hmd-accent)", full);
    }

    [Fact]
    public void Indent_LeerBeiTiefeNull_InvariantBeiMehr()
    {
        Assert.Equal(string.Empty, Heimdall.Blazor.HeimdallTraceWaterfall.Indent(0));
        Assert.Equal("margin-left:1.8rem", Heimdall.Blazor.HeimdallTraceWaterfall.Indent(2));
        Assert.DoesNotContain(",", Heimdall.Blazor.HeimdallTraceWaterfall.Indent(3));
    }
}