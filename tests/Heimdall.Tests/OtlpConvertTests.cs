using System.Linq;
using Google.Protobuf;
using Heimdall.Otlp;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OTelCollectorTrace = OpenTelemetry.Proto.Collector.Trace;
using OTelCollectorLogs = OpenTelemetry.Proto.Collector.Logs;
using OTelCollectorMetrics = OpenTelemetry.Proto.Collector.Metrics;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Verifiziert OtlpConvert (Pfad C-Eingang): OTLP-Collector-Requests (Traces/Logs/
/// Metrics) werden kanonisch in Heimdall-Records überführt — die Gegenrichtung zum
/// Sdk-Exporter. IDs bleiben rohe Bytes (kein Hex-Roundtrip), fehlerhafte Einträge
/// werden verworfen, nicht der ganze Batch.
/// </summary>
public class OtlpConvertTests
{
    // -----------------------------------------------------------------
    // Traces
    // -----------------------------------------------------------------

    [Fact]
    public void ToSpans_KonvertiertResourceScopeSpanAttributeEvent()
    {
        var tid = new byte[] { 0xa1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var sid = new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0x01 };

        var req = new OTelCollectorTrace.V1.ExportTraceServiceRequest();
        var rs = new ResourceSpans
        {
            Resource = new Resource
            {
                Attributes =
                {
                    new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "shop" } }
                }
            }
        };
        var ss = new ScopeSpans { Scope = new InstrumentationScope { Name = "api", Version = "1.0" } };
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(tid),
            SpanId = ByteString.CopyFrom(sid),
            Name = "checkout",
            Kind = SpanKind.Server,
            StartTimeUnixNano = 1_000_000_000UL,
            EndTimeUnixNano = 1_800_000_000UL,
            Status = new Status { Code = Status.Types.StatusCode.Ok, Message = "ok" },
            Attributes =
            {
                new KeyValue { Key = "http.method", Value = new AnyValue { StringValue = "GET" } }
            },
            Events =
            {
                new Span.Types.Event { TimeUnixNano = 1_200_000_000UL, Name = "cache.miss" }
            }
        };
        ss.Spans.Add(span);
        rs.ScopeSpans.Add(ss);
        req.ResourceSpans.Add(rs);

        var spans = OtlpConvert.ToSpans(req);
        var s = Assert.Single(spans);
        Assert.Equal("checkout", s.Name);
        Assert.Equal(HSpanKind.Server, s.Kind);
        Assert.Equal(HStatusCode.Ok, s.StatusCode);
        Assert.Equal("ok", s.StatusMessage);
        Assert.True(s.TraceId.SequenceEqual(tid));
        Assert.True(s.SpanId.SequenceEqual(sid));
        Assert.Equal(1_000_000_000L, s.StartUnixNano);
        Assert.Equal(1_800_000_000L, s.EndUnixNano);
        Assert.NotNull(s.Resource);
        Assert.Equal("shop", s.Resource!.Attributes.First(a => a.Key == "service.name").Value);
        Assert.NotNull(s.Scope);
        Assert.Equal("api", s.Scope!.Name);
        Assert.Contains(s.Attributes, a => a.Key == "http.method" && Equals(a.Value, "GET"));
        Assert.Single(s.Events);
        Assert.Equal("cache.miss", s.Events[0].Name);
        Assert.Null(s.ParentSpanId);
    }

    [Fact]
    public void ToSpans_SpanOhneTraceIdWirdVerworfen()
    {
        var req = new OTelCollectorTrace.V1.ExportTraceServiceRequest();
        var rs = new ResourceSpans();
        var ss = new ScopeSpans();
        ss.Spans.Add(new Span
        {
            TraceId = ByteString.Empty,   // keine TraceId → verwerfen
            SpanId = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
            Name = "x",
        });
        ss.Spans.Add(new Span
        {
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 }),
            Name = "y",
        });
        rs.ScopeSpans.Add(ss);
        req.ResourceSpans.Add(rs);
        // „x" (leere TraceId) wird verworfen; „y" (16 Null-Bytes, nicht leer) bleibt.
        var spans = OtlpConvert.ToSpans(req);
        Assert.Single(spans);
        Assert.Equal("y", spans[0].Name);
    }

    [Fact]
    public void ToSpans_NullRequest_LiefertLeer()
    {
        Assert.Empty(OtlpConvert.ToSpans(null!));
    }

    // -----------------------------------------------------------------
    // Logs
    // -----------------------------------------------------------------

    [Fact]
    public void ToLogs_MapptSeverityBodyTraceId()
    {
        var tid = new byte[16]; tid[0] = 0xb2;
        var sid = new byte[8];

        var req = new OTelCollectorLogs.V1.ExportLogsServiceRequest();
        var rl = new ResourceLogs();
        var sl = new ScopeLogs { Scope = new InstrumentationScope { Name = "api" } };
        sl.LogRecords.Add(new LogRecord
        {
            TimeUnixNano = 1_500_000_000UL,
            SeverityNumber = SeverityNumber.Error,   // 17 → HSeverity.Error
            SeverityText = "ERROR",
            Body = new AnyValue { StringValue = "db timeout" },
            TraceId = ByteString.CopyFrom(tid),
            SpanId = ByteString.CopyFrom(sid),
            Attributes =
            {
                new KeyValue { Key = "db.system", Value = new AnyValue { StringValue = "sqlite" } }
            }
        });
        rl.ScopeLogs.Add(sl);
        req.ResourceLogs.Add(rl);

        var logs = OtlpConvert.ToLogs(req);
        var l = Assert.Single(logs);
        Assert.Equal(1_500_000_000L, l.TimeUnixNano);
        Assert.Equal(HSeverity.Error, l.Severity);
        Assert.Equal("ERROR", l.SeverityText);
        Assert.Equal("db timeout", l.Body);
        Assert.True(l.TraceId!.SequenceEqual(tid));
        Assert.Contains(l.Attributes, a => a.Key == "db.system" && Equals(a.Value, "sqlite"));
    }

    [Fact]
    public void ToLogs_SeverityBands()
    {
        int SevOf(SeverityNumber sn)
        {
            var req = new OTelCollectorLogs.V1.ExportLogsServiceRequest();
            var rl = new ResourceLogs();
            var sl = new ScopeLogs();
            sl.LogRecords.Add(new LogRecord { SeverityNumber = sn, TimeUnixNano = 1 });
            rl.ScopeLogs.Add(sl);
            req.ResourceLogs.Add(rl);
            return (int)OtlpConvert.ToLogs(req)[0].Severity;
        }
        Assert.Equal((int)HSeverity.Trace, SevOf(SeverityNumber.Trace));
        Assert.Equal((int)HSeverity.Debug, SevOf(SeverityNumber.Debug));
        Assert.Equal((int)HSeverity.Info, SevOf(SeverityNumber.Info));
        Assert.Equal((int)HSeverity.Warn, SevOf(SeverityNumber.Warn));
        Assert.Equal((int)HSeverity.Error, SevOf(SeverityNumber.Error));
        Assert.Equal((int)HSeverity.Fatal, SevOf(SeverityNumber.Fatal));
        // Unspecified (0) → Info-Band.
        Assert.Equal((int)HSeverity.Info, SevOf(SeverityNumber.Unspecified));
    }

    // -----------------------------------------------------------------
    // Metrics
    // -----------------------------------------------------------------

    [Fact]
    public void ToMetrics_GaugeSumHistogram()
    {
        var req = new OTelCollectorMetrics.V1.ExportMetricsServiceRequest();
        var rm = new ResourceMetrics();
        var sm = new ScopeMetrics();

        // Gauge: as_double
        var gauge = new Metric { Name = "cpu.load", Unit = "%" };
        gauge.Gauge = new Gauge();
        gauge.Gauge.DataPoints.Add(new NumberDataPoint
        {
            TimeUnixNano = 1_000_000_000UL,
            AsDouble = 42.5,
            Attributes = { new KeyValue { Key = "host", Value = new AnyValue { StringValue = "h1" } } }
        });

        // Sum: as_int, Cumulative
        var sum = new Metric { Name = "orders", Unit = "1" };
        sum.Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative };
        sum.Sum.DataPoints.Add(new NumberDataPoint
        {
            TimeUnixNano = 2_000_000_000UL,
            AsInt = 123L,
        });

        // Histogram: Delta, Buckets
        var hist = new Metric { Name = "http.server.request.duration", Unit = "s" };
        hist.Histogram = new Histogram { AggregationTemporality = AggregationTemporality.Delta };
        var hdp = new HistogramDataPoint
        {
            TimeUnixNano = 3_000_000_000UL,
            Count = 100UL,
            Sum = 5.0,
            Min = 0.0,
            Max = 0.05,
        };
        hdp.BucketCounts.Add(30UL); hdp.BucketCounts.Add(30UL); hdp.BucketCounts.Add(30UL);
        hdp.BucketCounts.Add(10UL);
        for (int i = 0; i < hdp.BucketCounts.Count - 1; i++)
            hdp.ExplicitBounds.Add(0.005 * (i + 1));   // 4 Counts → 3 Bounds
        hist.Histogram.DataPoints.Add(hdp);

        sm.Metrics.Add(gauge);
        sm.Metrics.Add(sum);
        sm.Metrics.Add(hist);
        rm.ScopeMetrics.Add(sm);
        req.ResourceMetrics.Add(rm);

        var metrics = OtlpConvert.ToMetrics(req);
        Assert.Equal(3, metrics.Count);

        var g = metrics.Single(m => m.Name == "cpu.load");
        Assert.Equal(HMetricType.Gauge, g.Type);
        Assert.Equal(HTemporality.Unspecified, g.Temporality);
        Assert.Equal(42.5, g.Value);

        var s = metrics.Single(m => m.Name == "orders");
        Assert.Equal(HMetricType.Sum, s.Type);
        Assert.Equal(HTemporality.Cumulative, s.Temporality);
        Assert.Equal(123.0, s.Value);   // AsInt → double

        var h = metrics.Single(m => m.Name == "http.server.request.duration");
        Assert.Equal(HMetricType.Histogram, h.Type);
        Assert.Equal(HTemporality.Delta, h.Temporality);
        Assert.Equal(100L, h.Count);
        Assert.Equal(5.0, h.Sum);
        Assert.Equal(0.0, h.Min);
        Assert.Equal(0.05, h.Max);
        Assert.Equal(4, h.BucketCounts!.Count);
        Assert.Equal(3, h.ExplicitBounds!.Count);
        Assert.Equal(100L, h.BucketCounts.Sum(x => x));
    }

    [Fact]
    public void ToMetrics_NullRequest_LiefertLeer()
    {
        Assert.Empty(OtlpConvert.ToMetrics(null!));
    }

    [Fact]
    public void ToLogs_NullRequest_LiefertLeer()
    {
        Assert.Empty(OtlpConvert.ToLogs(null!));
    }
}