using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Heimdall.Prometheus;
using Xunit;

namespace Heimdall.Tests;

// ---------------------------------------------------------------------------
// RedMetricsProvider + CompositeMetricSource-Tests (Phase 4). Ein In-Memory-
// FakeQuery liefert Server-SpanRows; RED leitet http_requests_total und
// http_request_duration_seconds_* ab. Geprueft wird ueber PromEngine + Composite
// (real leer + RED), damit auch Discovery und Namens-Mapping end-to-end laufen.
// ---------------------------------------------------------------------------

public class RedMetricsProviderTests
{
    private const long S = 1_000_000_000L; // 1 Sekunde in ns

    private sealed class FakeQuery : IHeimdallQuery
    {
        public readonly List<SpanRow> Spans = new();

        public IReadOnlyList<SpanRow> ListSpans(SpanFilter filter)
        {
            long from = filter.FromUnixNano ?? 0;
            long to = filter.ToUnixNano ?? long.MaxValue;
            var q = Spans.Where(s =>
                (!filter.Kind.HasValue || s.Kind == filter.Kind.Value) &&
                s.StartUnixNano >= from && s.StartUnixNano <= to);
            if (filter.Offset > 0) q = q.Skip(filter.Offset);
            return q.Take(filter.Limit).ToArray();
        }

        public long CountSpans() => Spans.Count;
        public long CountLogs() => 0;
        public long CountMetrics() => 0;
        public IReadOnlyList<TraceSummary> ListTraces(TraceFilter filter) => Array.Empty<TraceSummary>();
        public IReadOnlyList<SpanRow> GetTrace(string traceId) => Array.Empty<SpanRow>();
        public IReadOnlyList<LogRow> SearchLogs(LogSearch search) => Array.Empty<LogRow>();
        public IReadOnlyList<MetricRow> MetricSeries(string name, long? f, long? t, int limit = 500) => Array.Empty<MetricRow>();
    }

    private sealed class EmptySource : IHeimdallMetricSource
    {
        public IReadOnlyList<string> ListMetricNames(long? f = null, long? t = null) => Array.Empty<string>();
        public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null) => Array.Empty<string>();
        public IReadOnlyList<string> ListLabelValues(string n, IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null) => Array.Empty<string>();
        public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery q) => Array.Empty<HMetricPointView>();
    }

    private static SpanRow ServerSpan(long startNs, double durationSec, string route, string method, string status, string job = "shop", int statusCode = 1)
    {
        long durNs = (long)(durationSec * S);
        string attrs = $"{{\"http.route\":\"{route}\",\"http.method\":\"{method}\",\"http.response.status_code\":\"{status}\"}}";
        string res = $"{{\"service.name\":\"{job}\"}}";
        return new SpanRow("t" + startNs, "s" + startNs, string.Empty, "GET " + route,
            (int)HSpanKind.Server, startNs, startNs + durNs, durNs, statusCode, null, attrs, "[]", res, "mvc");
    }

    private static PromEngine Engine(FakeQuery q) => new(new CompositeMetricSource(new EmptySource(), new RedMetricsProvider(q)));

    // --- Namen + Discovery --------------------------------------------------
    [Fact]
    public void Red_ListetAbleitbareMetriknamen()
    {
        var q = new FakeQuery();
        var red = new RedMetricsProvider(q);
        Assert.Contains("http_requests", red.ListMetricNames());
        Assert.Contains("http_request_duration", red.ListMetricNames());
    }

    [Fact]
    public void Red_PromNamenUeberComposite()
    {
        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/orders", "GET", "200"));
        var eng = Engine(q);
        var names = eng.ListMetricNames(0, 200 * S);
        Assert.Contains("http_requests_total", names);
        Assert.Contains("http_request_duration_seconds_bucket", names);
        Assert.Contains("http_request_duration_seconds_sum", names);
        Assert.Contains("http_request_duration_seconds_count", names);
    }

    [Fact]
    public void Red_LabelsEnthaltenJobRouteMethodStatus()
    {
        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/orders", "GET", "200"));
        var eng = Engine(q);
        var labels = eng.ListLabelNames(0, 200 * S);
        Assert.Contains("job", labels);                 // service.name -> job
        Assert.Contains("http_route", labels);
        Assert.Contains("http_method", labels);
        Assert.Contains("http_response_status_code", labels);
    }

    [Fact]
    public void Red_JobWerteVonServiceName()
    {
        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/orders", "GET", "200", "shop"));
        q.Spans.Add(ServerSpan(101 * S, 0.02, "/api/users", "GET", "200", "billing"));
        var eng = Engine(q);
        var jobs = eng.ListLabelValues("job", 0, 200 * S);
        Assert.Contains("shop", jobs);
        Assert.Contains("billing", jobs);
    }

    // --- Counter ------------------------------------------------------------
    [Fact]
    public void Red_HttpRequestsTotalKumuliertDreiSpans()
    {
        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/orders", "GET", "200"));
        q.Spans.Add(ServerSpan(100 * S + 100_000, 0.02, "/api/orders", "GET", "200")); // gleiche Sekunde
        q.Spans.Add(ServerSpan(100 * S + 200_000, 0.02, "/api/orders", "GET", "200"));
        var eng = Engine(q);
        var res = eng.EvalInstant("http_requests_total", 101_000); // ms, innerhalb Lookback
        Assert.Equal(PromResultKind.Vector, res.Kind);
        Assert.Single(res.Vector!.Samples);
        Assert.Equal(3.0, res.Vector.Samples[0].Value);
    }

    [Fact]
    public void Red_HttpRequestsTotalGruppiertNachRouteStatus()
    {
        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/orders", "GET", "200"));
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/orders", "GET", "500"));
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/users", "GET", "200"));
        var eng = Engine(q);
        var res = eng.EvalInstant("http_requests_total", 101_000);
        // Drei Gruppen: (orders,200), (orders,500), (users,200) -> je 1.
        Assert.Equal(3, res.Vector!.Samples.Count);
        foreach (var s in res.Vector.Samples) Assert.Equal(1.0, s.Value);
    }

    [Fact]
    public void Red_RateUeberZweiBuckets()
    {
        // Zwei Sekunden-Buckets mit je 2 Requests -> kumuliert 2, 4.
        // rate[5s] = (4-2)/5 = 0.4/s (Prom: Increase / Fenstergroesse).
        var q = new FakeQuery();
        for (int i = 0; i < 2; i++) q.Spans.Add(ServerSpan(100 * S + i * 1_000, 0.01, "/api", "GET", "200"));
        for (int i = 0; i < 2; i++) q.Spans.Add(ServerSpan(102 * S + i * 1_000, 0.01, "/api", "GET", "200"));
        var eng = Engine(q);
        var res = eng.EvalInstant("rate(http_requests_total[5s])", 105_000);
        Assert.Equal(PromResultKind.Vector, res.Kind);
        Assert.Equal(0.4, res.Vector!.Samples[0].Value, 6);
    }

    // --- Histogramm / histogram_quantile ------------------------------------
    [Fact]
    public void Red_HistogramQuantileP95()
    {
        // Dauern 0.02, 0.2, 2.0 s in einer Gruppe/Secunde.
        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api", "GET", "200"));
        q.Spans.Add(ServerSpan(100 * S + 1_000, 0.2, "/api", "GET", "200"));
        q.Spans.Add(ServerSpan(100 * S + 2_000, 2.0, "/api", "GET", "200"));
        var eng = Engine(q);
        var res = eng.EvalInstant("histogram_quantile(0.95, http_request_duration_seconds_bucket)", 101_000);
        Assert.Equal(PromResultKind.Vector, res.Kind);
        // rank=0.95*3=2.85 -> zwischen le=1 (cum=2) und le=2.5 (cum=3): 1 + 0.85*1.5 = 2.275.
        Assert.Equal(2.275, res.Vector!.Samples[0].Value, 3);
    }

    [Fact]
    public void Red_HistogramSumUndCount()
    {
        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api", "GET", "200"));
        q.Spans.Add(ServerSpan(100 * S + 1_000, 0.2, "/api", "GET", "200"));
        var eng = Engine(q);
        var sumRes = eng.EvalInstant("http_request_duration_seconds_sum", 101_000);
        var cntRes = eng.EvalInstant("http_request_duration_seconds_count", 101_000);
        Assert.Equal(0.22, sumRes.Vector!.Samples[0].Value, 6);
        Assert.Equal(2.0, cntRes.Vector!.Samples[0].Value);
    }

    // --- Status-Fallback ----------------------------------------------------
    [Fact]
    public void Red_StatusFallbackAusStatusCode()
    {
        // Kein http.response.status_code im AttrsJson, dafuer StatusCode=Error -> 500.
        var q = new FakeQuery();
        long start = 100 * S;
        long dur = 10_000_000;
        string attrs = "{\"http.route\":\"/api\",\"http.method\":\"GET\"}";
        string res = "{\"service.name\":\"shop\"}";
        q.Spans.Add(new SpanRow("t", "s", "", "GET /api", (int)HSpanKind.Server, start, start + dur, dur,
            (int)HStatusCode.Error, null, attrs, "[]", res, "mvc"));
        var eng = Engine(q);
        var statuses = eng.ListLabelValues("http_response_status_code", 0, 200 * S);
        Assert.Contains("500", statuses);
    }

    // --- Composite-Routing --------------------------------------------------
    [Fact]
    public void Composite_RoutetFremdmetrikAnRealUndRedAnRed()
    {
        // Real-Source mit einem orders-Counter, RED mit einem Span.
        var realPts = new List<HMetricPointView>();
        var realLabels = new Dictionary<string, string> { ["service.name"] = "shop" };
        realPts.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
            100 * S, 42, null, null, null, null, null, null, realLabels, "api"));
        var real = new SimpleSource(realPts);

        var q = new FakeQuery();
        q.Spans.Add(ServerSpan(100 * S, 0.02, "/api/orders", "GET", "200"));

        var eng = new PromEngine(new CompositeMetricSource(real, new RedMetricsProvider(q)));

        var orders = eng.EvalInstant("orders_total", 101_000);
        Assert.Equal(42.0, orders.Vector!.Samples[0].Value);

        var red = eng.EvalInstant("http_requests_total", 101_000);
        Assert.Equal(1.0, red.Vector!.Samples[0].Value);
    }

    private sealed class SimpleSource : IHeimdallMetricSource
    {
        private readonly List<HMetricPointView> _pts;
        public SimpleSource(List<HMetricPointView> pts) { _pts = pts; }
        public IReadOnlyList<string> ListMetricNames(long? f = null, long? t = null)
            => _pts.Select(p => p.Name).Distinct().ToArray();
        public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => _pts.SelectMany(p => p.Labels.Keys).Distinct().ToArray();
        public IReadOnlyList<string> ListLabelValues(string n, IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => _pts.Where(p => p.Labels.ContainsKey(n)).Select(p => p.Labels[n]).Distinct().ToArray();
        public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery q)
            => _pts.Where(p => q.Names.Contains(p.Name)).ToArray();
    }
}