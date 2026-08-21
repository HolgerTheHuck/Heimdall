using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using Heimdall;
using Heimdall.Sdk;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;
using OtelSdk = OpenTelemetry.Sdk;

namespace Heimdall.Tests;

/// <summary>
/// Verifiziert Heimdall.Sdk (Pfad A): der in-process OTel-SDK-Exporter leitet
/// bestehende SDK-Instrumentation ohne OTLP/HTTP/gRPC direkt in den Heimdall-Sink.
/// </summary>
public class HeimdallSdkTests
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

    private static List<HSpan> DrainSpans(CapturingSink s) { lock (s.Spans) return s.Spans.ToList(); }
    private static List<HMetricPoint> DrainMetrics(CapturingSink s) { lock (s.Metrics) return s.Metrics.ToList(); }

    [Fact]
    public void Traces_Flow_Directly_Into_Heimdall_Sink()
    {
        var sink = new CapturingSink();
        using var provider = OtelSdk.CreateTracerProviderBuilder()
            .AddSource("heimdall.sdk.test")
            .UseHeimdallExporter(sink, "shop", "1.0")
            .Build()!;

        using var src = new ActivitySource("heimdall.sdk.test");
        using (var a = src.StartActivity("checkout")!)
        {
            a.SetTag("http.method", "GET");
            a.SetStatus(ActivityStatusCode.Ok);
        }
        Assert.True(provider.ForceFlush());

        var spans = DrainSpans(sink);
        var span = Assert.Single(spans);
        Assert.Equal("checkout", span.Name);
        Assert.Equal(HSpanKind.Internal, span.Kind);            // SDK-Default ohne Kind
        Assert.Equal(HStatusCode.Ok, span.StatusCode);
        Assert.Equal(16, span.TraceId.Length);
        Assert.Equal(8, span.SpanId.Length);
        Assert.NotNull(span.Resource);
        Assert.Equal("shop", span.Resource!.Attributes.First(a => a.Key == "service.name").Value);
        Assert.Contains(span.Attributes, a => a.Key == "http.method" && Equals(a.Value, "GET"));
    }

    [Fact]
    public void Child_Spans_Inherit_Trace_Id_From_Sdk()
    {
        var sink = new CapturingSink();
        using var provider = OtelSdk.CreateTracerProviderBuilder()
            .AddSource("heimdall.sdk.test")
            .UseHeimdallExporter(sink, "svc")
            .Build()!;
        using var src = new ActivitySource("heimdall.sdk.test");

        using (src.StartActivity("parent")!)
        using (src.StartActivity("child")!)
        {
            // Kind: Internal
        }
        Assert.True(provider.ForceFlush());

        var spans = DrainSpans(sink);
        Assert.Equal(2, spans.Count);
        var parent = spans.Single(s => s.Name == "parent");
        var child = spans.Single(s => s.Name == "child");
        Assert.Equal(parent.TraceId, child.TraceId);
        Assert.True(child.ParentSpanId is not null);
        Assert.True(child.ParentSpanId!.SequenceEqual(parent.SpanId));
    }

    [Fact]
    public void Metrics_Flow_As_Cumulative_Sum_Points()
    {
        var sink = new CapturingSink();
        using var provider = OtelSdk.CreateMeterProviderBuilder()
            .AddMeter("heimdall.sdk.test")
            .UseHeimdallExporter(sink, "shop")
            .Build()!;
        using var meter = new Meter("heimdall.sdk.test");
        var counter = meter.CreateCounter<long>("orders");
        counter.Add(1, new KeyValuePair<string, object?>("region", "eu"));
        counter.Add(2, new KeyValuePair<string, object?>("region", "eu"));
        Assert.True(provider.ForceFlush());

        var metrics = DrainMetrics(sink);
        Assert.Contains(metrics, m => m.Name == "orders" && m.Type == HMetricType.Sum
                                       && m.Value == 3d && m.Temporality == HTemporality.Cumulative);
    }

    [Fact]
    public void Histogram_Points_Carry_Buckets()
    {
        var sink = new CapturingSink();
        using var provider = OtelSdk.CreateMeterProviderBuilder()
            .AddMeter("heimdall.sdk.test")
            .UseHeimdallExporter(sink, "shop")
            .Build()!;
        using var meter = new Meter("heimdall.sdk.test");
        var hist = meter.CreateHistogram<double>("latency_ms");
        hist.Record(5, new KeyValuePair<string, object?>("route", "/api"));
        hist.Record(25, new KeyValuePair<string, object?>("route", "/api"));
        Assert.True(provider.ForceFlush());

        var metrics = DrainMetrics(sink);
        var h = Assert.Single(metrics, m => m.Name == "latency_ms" && m.Type == HMetricType.Histogram);
        Assert.Equal(2L, h.Count);
        Assert.Equal(30d, h.Sum);
        Assert.NotNull(h.BucketCounts);
        Assert.True(h.BucketCounts!.Sum(x => x) >= h.Count);
    }
}