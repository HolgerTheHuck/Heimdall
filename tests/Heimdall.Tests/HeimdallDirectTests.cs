using System;
using System.Collections.Generic;
using System.Linq;
using Heimdall;
using Heimdall.Direct;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Verifiziert Heimdall.Direct: Span-Erzeugung inkl. Parent-Verkettung,
/// Log-Verknuepfung mit aktivem Span, Metrik-Emission und Rekursionsschutz.
/// Nutzt einen abfangenden IHeimdallSink, um die erzeugten Records zu pruefen.
/// </summary>
public class HeimdallDirectTests
{
    private sealed class CapturingSink : IHeimdallSink
    {
        public List<HSpan> Spans = new();
        public List<HLogRecord> Logs = new();
        public List<HMetricPoint> Metrics = new();
        public void WriteSpans(IReadOnlyList<HSpan> s) { lock (Spans) Spans.AddRange(s); }
        public void WriteLogs(IReadOnlyList<HLogRecord> l) { lock (Logs) Logs.AddRange(l); }
        public void WriteMetrics(IReadOnlyList<HMetricPoint> m) { lock (Metrics) Metrics.AddRange(m); }
    }

    private static HSpan Single(CapturingSink s) { lock (s.Spans) return s.Spans.Last(); }

    [Fact]
    public void Span_Ends_And_Writes_Once()
    {
        var sink = new CapturingSink();
        using var hub = new HeimdallHub(sink, new HResource(new[] { new HAttribute("service.name", "svc") }));
        var tracer = hub.GetTracer("test");

        using (tracer.StartSpan("outer"))
        {
            // aktiver Span ist Current; ein Log verknuepft sich automatisch.
            hub.GetLogger("test").Emit(HSeverity.Info, "hello", new HAttribute("k", "v"));
        }

        Assert.Single(sink.Spans);
        Assert.Single(sink.Logs);
        var span = Single(sink);
        Assert.Equal("outer", span.Name);
        Assert.Equal(HStatusCode.Unset, span.StatusCode);
        Assert.NotNull(span.Resource);
        Assert.Equal("svc", span.Resource!.Attributes.First(a => a.Key == "service.name").Value);

        var log = sink.Logs[0];
        Assert.Equal("hello", log.Body);
        Assert.True(log.TraceId is not null && log.TraceId.Length == 16);
        Assert.True(log.SpanId is not null && log.SpanId!.SequenceEqual(span.SpanId));
    }

    [Fact]
    public void Child_Span_Inherits_Trace_And_Parent()
    {
        var sink = new CapturingSink();
        using var hub = new HeimdallHub(sink);
        var tracer = hub.GetTracer("test");

        using (var outer = tracer.StartSpan("outer"))
        using (tracer.StartSpan("inner"))
        {
            // inner wird automatisch an outer (Current) angehaengt.
        }

        Assert.Equal(2, sink.Spans.Count);
        var sInner = sink.Spans[0]; // inner endet zuerst -> zuerst geschrieben
        var sOuter = sink.Spans[1];
        Assert.Equal(sOuter.TraceId, sInner.TraceId);
        Assert.True(sInner.ParentSpanId is not null);
        Assert.True(sInner.ParentSpanId!.SequenceEqual(sOuter.SpanId));
    }

    [Fact]
    public void Metrics_Emit_Points()
    {
        var sink = new CapturingSink();
        using var hub = new HeimdallHub(sink);
        var meter = hub.GetMeter("test");
        var counter = meter.CreateCounter("orders");
        var gauge = meter.CreateGauge("queue_depth");
        var hist = meter.CreateHistogram("latency_ms");

        counter.Add(1, new HAttribute("region", "eu"));
        counter.Add(2, new HAttribute("region", "eu"));
        gauge.Set(5);
        hist.Record(42);

        Assert.Equal(2, sink.Metrics.Count(m => m.Name == "orders"));
        Assert.Equal(3d, sink.Metrics.Last(m => m.Name == "orders").Value);
        Assert.Equal(HMetricType.Sum, sink.Metrics.First(m => m.Name == "orders").Type);

        var g = sink.Metrics.Single(m => m.Name == "queue_depth");
        Assert.Equal(5d, g.Value);
        Assert.Equal(HMetricType.Gauge, g.Type);

        var h = sink.Metrics.Single(m => m.Name == "latency_ms");
        Assert.Equal(HMetricType.Histogram, h.Type);
        Assert.Equal(1L, h.Count);
        Assert.Equal(42d, h.Sum);
        Assert.NotNull(h.BucketCounts);
        Assert.True(h.BucketCounts!.Sum(x => x) >= h.Count); // kumulativ >= count
    }

    [Fact]
    public void Suppressed_Scope_Produces_Nothing()
    {
        var sink = new CapturingSink();
        using var hub = new HeimdallHub(sink);
        var tracer = hub.GetTracer("test");

        using (HeimdallRecording.SuppressScope())
        {
            using (tracer.StartSpan("x")) { }
            hub.GetLogger("t").Emit(HSeverity.Warn, "y");
            hub.GetMeter("t").CreateCounter("c").Add(1);
        }

        Assert.Empty(sink.Spans);
        Assert.Empty(sink.Logs);
        Assert.Empty(sink.Metrics);
    }

    [Fact]
    public void Noop_Hub_Has_Zero_Overhead()
    {
        // Der statische Noop-Hub muss ohne Sink funktionieren und nichts werfen.
        IHeimdallHub hub = HeimdallNoop.Hub;
        using (hub.GetTracer("t").StartSpan("s")) { }
        hub.GetLogger("t").Emit(HSeverity.Info, "x");
        hub.GetMeter("t").CreateCounter("c").Add(1);
        Assert.Same(HeimdallNoop.Tracer, hub.GetTracer("t"));
    }
}