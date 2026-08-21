using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Heimdall.Prometheus;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Heimdall.Tests;

// ---------------------------------------------------------------------------
// Prometheus-HTTP-Envelope-Tests. Die Handler werden direkt mit einem
// DefaultHttpContext (Memory-Body) aufgerufen; geprüft werden die Prom-JSON-
// Shapes (status/data/error), Werte-als-String-Konvention und die Grafana-
// Connect-Endpunkte (/status/buildinfo, /labels, /label/.../values, /series,
// /metadata). Kein echter Socket — die Handler sind reine Funktionen.
// ---------------------------------------------------------------------------

public class PromHttpTests
{
    private const long S = 1_000_000_000L;

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
            => Points.Where(p => q.Names.Contains(p.Name)).OrderBy(p => p.TimeUnixNano).ToArray();
    }

    private static PromEngine EngineWithOrders()
    {
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "shop", ["region"] = "eu" };
        src.Points.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
            S, 46, null, null, null, null, null, null, labels, "api"));
        return new PromEngine(src);
    }

    private static async Task<string> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await result.ExecuteAsync(ctx);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static HttpRequest QueryRequest(params (string k, string v)[] kv)
    {
        var ctx = new DefaultHttpContext();
        var qs = QueryString.Create(kv.Select(p => new KeyValuePair<string, string?>(p.k, p.v)));
        ctx.Request.QueryString = qs;
        return ctx.Request;
    }

    // === /status/buildinfo =================================================
    [Fact]
    public async Task BuildInfo_ErfolgsEnvelopeMitVersion()
    {
        var body = await ExecuteAsync(PromHttpHandlers.BuildInfo());
        Assert.Contains("\"status\":\"success\"", body);
        Assert.Contains("\"version\":\"0.1.0\"", body);
        Assert.Contains("\"data\":", body);
    }

    // === /query (Vektor) ===================================================
    [Fact]
    public async Task Query_Vektor_WertAlsStringUndJobLabel()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.Query(eng, QueryRequest(("query", "orders_total"), ("time", "1"))));
        Assert.Contains("\"resultType\":\"vector\"", body);
        Assert.Contains("\"__name__\":\"orders_total\"", body);
        Assert.Contains("\"job\":\"shop\"", body);
        Assert.Contains("\"46\"", body); // Wert als String
    }

    [Fact]
    public async Task Query_Skalar_Literal()
    {
        var eng = new PromEngine(new FakeSource());
        var body = await ExecuteAsync(PromHttpHandlers.Query(eng, QueryRequest(("query", "3.5"))));
        Assert.Contains("\"resultType\":\"scalar\"", body);
        Assert.Contains("\"3.5\"", body);
    }

    [Fact]
    public async Task Query_Leer_LiefertBadData()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.Query(eng, QueryRequest()));
        Assert.Contains("\"status\":\"error\"", body);
        Assert.Contains("\"errorType\":\"bad_data\"", body);
    }

    [Fact]
    public async Task Query_BoesesPromQL_LiefertBadDataEnvelope()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.Query(eng, QueryRequest(("query", "rate("))));
        Assert.Contains("\"errorType\":\"bad_data\"", body);
    }

    // === /query_range ======================================================
    [Fact]
    public async Task QueryRange_LiefertMatrix()
    {
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "shop" };
        int[] vals = { 10, 21, 33, 46 };
        for (int i = 0; i < vals.Length; i++)
            src.Points.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
                i * S, vals[i], null, null, null, null, null, null, labels, "api"));
        var eng = new PromEngine(src);
        var body = await ExecuteAsync(PromHttpHandlers.QueryRange(eng,
            QueryRequest(("query", "rate(orders_total[1m])"), ("start", "0"), ("end", "3"), ("step", "1"))));
        Assert.Contains("\"resultType\":\"matrix\"", body);
        Assert.Contains("\"values\"", body);
    }

    // === /labels ===========================================================
    [Fact]
    public async Task Labels_EnthaeltJobStattServiceName()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.Labels(eng, QueryRequest()));
        Assert.Contains("\"job\"", body);
        Assert.Contains("\"region\"", body);
        Assert.DoesNotContain("service.name", body);
    }

    // === /label/{name}/values =============================================
    [Fact]
    public async Task LabelValues_Job_LiefertShop()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.LabelValues(eng, QueryRequest(), "job"));
        Assert.Contains("\"shop\"", body);
    }

    // === /series ===========================================================
    [Fact]
    public async Task Series_MatchSelektor_LiefertLabelset()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.Series(eng,
            QueryRequest(("match[]", "orders_total"), ("start", "0"), ("end", "9999999999"))));
        Assert.Contains("\"__name__\":\"orders_total\"", body);
        Assert.Contains("\"job\":\"shop\"", body);
    }

    [Fact]
    public async Task Series_OhneMatch_LiefertBadData()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.Series(eng, QueryRequest()));
        Assert.Contains("\"errorType\":\"bad_data\"", body);
    }

    // === /metadata =========================================================
    [Fact]
    public async Task Metadata_LiefertTyp()
    {
        var eng = EngineWithOrders();
        var body = await ExecuteAsync(PromHttpHandlers.Metadata(eng, QueryRequest()));
        Assert.Contains("\"orders_total\"", body);
        Assert.Contains("\"type\":\"counter\"", body);
    }

    // === /status/runtimeinfo + /metrics ===================================
    [Fact]
    public async Task RuntimeInfo_ErfolgsEnvelope()
    {
        var body = await ExecuteAsync(PromHttpHandlers.RuntimeInfo());
        Assert.Contains("\"status\":\"success\"", body);
    }

    [Fact]
    public async Task Metrics_TextExposition()
    {
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "shop", ["region"] = "eu" };
        long nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        src.Points.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
            nowNs, 46, null, null, null, null, null, null, labels, "api"));
        var eng = new PromEngine(src);

        var ctx = new DefaultHttpContext();
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await PromHttpHandlers.Metrics(eng).ExecuteAsync(ctx);
        var body = Encoding.UTF8.GetString(ms.ToArray());

        Assert.StartsWith("text/plain", ctx.Response.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# TYPE orders_total counter", body);
        Assert.Contains("orders_total{", body);
        Assert.Contains("job=\"shop\"", body);
        Assert.Contains(" 46 ", body); // Wert + ms-Zeitstempel
    }

    // === Zeitparser ========================================================
    [Fact]
    public void ParseTimeMs_Rfc3339UndUnixSekunden()
    {
        Assert.Equal(1_000L, PromHttpHandlers.ParseTimeMs("1", null));
        Assert.Equal(1_500L, PromHttpHandlers.ParseTimeMs("1.5", null));
        var dto = PromHttpHandlers.ParseTimeMs("2024-01-01T00:00:00Z", null);
        Assert.True(dto.HasValue && dto.Value > 0);
    }

    [Fact]
    public void ParseDurationMs_PromEinheiten()
    {
        Assert.Equal(60_000L, Lexer.TryParseDurationMs("1m"));
        Assert.Equal(3_600_000L, Lexer.TryParseDurationMs("1h"));
        Assert.Equal(90_000L, Lexer.TryParseDurationMs("1m30s"));
        Assert.Null(Lexer.TryParseDurationMs("1x"));
    }
}