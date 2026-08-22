using System.Collections.Generic;
using Heimdall.Blazor.Grafana;
using Heimdall.Prometheus;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer <see cref="GrafanaTemplating"/>: Interpolation von
/// <c>$var</c>/<c>${var}</c>, Multi-Selektion → Regex-Alternation,
/// <c>$__all</c> → <c>.*</c>, label_values-Parser und Options-Aufloesung.
/// </summary>
public class GrafanaTemplatingTests
{
    // --- Interpolate -------------------------------------------------------

    [Fact]
    public void Interpolate_ErsetztDollarUndGeschweift()
    {
        var vars = new Dictionary<string, string> { ["job"] = "shop", ["http_route"] = "/api/orders" };
        Assert.Equal("sum(http_requests_total{job=~\"shop\",http_route=~\"/api/orders\"})",
            GrafanaTemplating.Interpolate("sum(http_requests_total{job=~\"$job\",http_route=~\"$http_route\"})", vars));
        Assert.Equal("sum(http_requests_total{job=~\"shop\"})",
            GrafanaTemplating.Interpolate("sum(http_requests_total{job=~\"${job}\"})", vars));
    }

    [Fact]
    public void Interpolate_All_LiefertRegexWildcard()
    {
        var vars = new Dictionary<string, string> { ["job"] = "$__all" };
        Assert.Equal("sum(http_requests_total{job=~\".*\"})",
            GrafanaTemplating.Interpolate("sum(http_requests_total{job=~\"$job\"})", vars));
    }

    [Fact]
    public void Interpolate_Leer_LiefertWildcard()
    {
        var vars = new Dictionary<string, string> { ["job"] = "" };
        Assert.Equal("sum(http_requests_total{job=~\".*\"})",
            GrafanaTemplating.Interpolate("sum(http_requests_total{job=~\"$job\"})", vars));
    }

    [Fact]
    public void Interpolate_Multi_LiefertRegexAlternation()
    {
        var vars = new Dictionary<string, string> { ["job"] = "shop,billing" };
        Assert.Equal("sum(http_requests_total{job=~\"shop|billing\"})",
            GrafanaTemplating.Interpolate("sum(http_requests_total{job=~\"$job\"})", vars));
    }

    [Fact]
    public void Interpolate_UnbekannteVar_BleibtStehen()
    {
        var vars = new Dictionary<string, string> { ["job"] = "shop" };
        Assert.Equal("$other{job=~\"shop\"}",
            GrafanaTemplating.Interpolate("$other{job=~\"$job\"}", vars));
    }

    [Fact]
    public void Interpolate_KeineVars_LiefertUnveraendert()
    {
        Assert.Equal("sum(rate(x[5m]))", GrafanaTemplating.Interpolate("sum(rate(x[5m]))", null!));
        Assert.Equal("sum($x)", GrafanaTemplating.Interpolate("sum($x)", new Dictionary<string, string>(0)));
    }

    [Fact]
    public void Interpolate_NameMitSuffix_WirdNurAlsGanzesErkannt()
    {
        // $jobx darf nicht durch $job ersetzt werden.
        var vars = new Dictionary<string, string> { ["job"] = "shop" };
        Assert.Equal("$jobx shop", GrafanaTemplating.Interpolate("$jobx $job", vars));
    }

    [Fact]
    public void Interpolate_ModifierSyntax_WirdErkannt()
    {
        // ${percentile:value} (Grafana-Modifier-Syntax) muss wie ${percentile}
        // interpoliert werden — ohne diese Erkennung bliebe '$' stehen und der
        // PromQL-Parser crashte (z. B. in histogram_quantile(${percentile:value}, …)).
        var vars = new Dictionary<string, string> { ["percentile"] = "0.95" };
        Assert.Equal("histogram_quantile(0.95, sum(rate(x[2m])) by (le))",
            GrafanaTemplating.Interpolate("histogram_quantile(${percentile:value}, sum(rate(x[2m])) by (le))", vars));
        // Auch ohne Modifier und als $percentile.
        Assert.Equal("histogram_quantile(0.95, x)",
            GrafanaTemplating.Interpolate("histogram_quantile(${percentile}, x)", vars));
        Assert.Equal("histogram_quantile(0.95, x)",
            GrafanaTemplating.Interpolate("histogram_quantile($percentile, x)", vars));
    }

    [Fact]
    public void Interpolate_BuiltInInterval_WirdErsetzt()
    {
        // $__rate_interval / $__interval sind keine Templating-Variablen, sondern
        // Built-ins — sie müssen via vars-Dict (vom Host gesetzt) interpoliert werden.
        var vars = new Dictionary<string, string> { ["__rate_interval"] = "1m", ["__interval"] = "15s" };
        Assert.Equal("rate(http_requests_total[1m])",
            GrafanaTemplating.Interpolate("rate(http_requests_total[$__rate_interval])", vars));
        Assert.Equal("max_over_time(x[15s])",
            GrafanaTemplating.Interpolate("max_over_time(x[${__interval}])", vars));
    }

    [Fact]
    public void Interpolate_BuiltInRange_WirdErsetzt()
    {
        // $__range (der gesamte gewählte Zeitraum) steht in increase()/count_over_time()
        // — gnetId-19924-Pattern: increase(http_server_request_duration_seconds_count[$__range]).
        var vars = new Dictionary<string, string> { ["__range"] = "5m" };
        Assert.Equal("sum(ceil(increase(http_server_request_duration_seconds_count[5m])))",
            GrafanaTemplating.Interpolate(
                "sum(ceil(increase(http_server_request_duration_seconds_count[$__range])))", vars));
        Assert.Equal("count_over_time(x[5m])",
            GrafanaTemplating.Interpolate("count_over_time(x[${__range}])", vars));
    }

    [Fact]
    public void BuiltIns_EnthaeltRangeIntervalRateInterval()
    {
        // Regression: der Host muss $__range ins vars-Dict mischen, sonst bleiben
        // Stat-/Table-Panels mit increase(…[$__range]) auf „unexpected character '$'"
        // stehen (gnetId-19924: 6 von 10 Panels). BuiltIns liefert alle drei Built-ins.
        var b = GrafanaTemplating.BuiltIns(fromMs: 0, toMs: 300_000, stepMs: 15_000);
        Assert.Equal("15s", b["__interval"]);
        Assert.Equal("1m", b["__rate_interval"]);          // 4×15s=60s → Floor 1m
        Assert.Equal("5m", b["__range"]);                  // toMs−fromMs = 300 s
    }

    [Fact]
    public void BuiltIns_RangeFolgtDemZeitraum()
    {
        // $__range spiegelt den gewählten Zeitraum, nicht den Step — 1 h bzw. 24 h.
        Assert.Equal("1h", GrafanaTemplating.BuiltIns(0, 3_600_000, 30_000)["__range"]);
        Assert.Equal("24h", GrafanaTemplating.BuiltIns(0, 86_400_000, 60_000)["__range"]);
    }

    // --- Operator-Beförderung (= → =~ bei Regex-Werten) --------------------
    // Grafana befördert =/!= automatisch zu =~/!~, sobald eine Variable All- oder
    // Multi-Werte liefert (also einen Regex-Wert). Ohne dies würde
    // service_name=".*" als EXAKTER Match für den Text ".*" gewertet und keine
    // Serie treffen — der otel-dotnet-webapi-Fall (alle HTTP-Panels leer).

    [Fact]
    public void Interpolate_GleichOperatorBeiAll_WirdZuRegexOperator()
    {
        var vars = new Dictionary<string, string> { ["service_name"] = "$__all" };
        Assert.Equal("rate(x{service_name=~\".*\"}[$__rate_interval])",
            GrafanaTemplating.Interpolate("rate(x{service_name=\"$service_name\"}[$__rate_interval])", vars));
    }

    [Fact]
    public void Interpolate_GleichOperatorBeiLeer_WirdZuRegexOperator()
    {
        // Leer-Current (includeAll=false, current="") → SelectedValue="" → .* .
        var vars = new Dictionary<string, string> { ["service_name"] = "" };
        Assert.Equal("rate(x{service_name=~\".*\"}[$__rate_interval])",
            GrafanaTemplating.Interpolate("rate(x{service_name=\"$service_name\"}[$__rate_interval])", vars));
    }

    [Fact]
    public void Interpolate_GleichOperatorBeiMulti_WirdZuRegexOperator()
    {
        var vars = new Dictionary<string, string> { ["service_name"] = "shop,billing" };
        Assert.Equal("rate(x{service_name=~\"shop|billing\"}[$__rate_interval])",
            GrafanaTemplating.Interpolate("rate(x{service_name=\"$service_name\"}[$__rate_interval])", vars));
    }

    [Fact]
    public void Interpolate_UngleichOperatorBeiAll_WirdZuNichtRegexOperator()
    {
        var vars = new Dictionary<string, string> { ["service_name"] = "$__all" };
        Assert.Equal("rate(x{service_name!~\".*\"}[$__rate_interval])",
            GrafanaTemplating.Interpolate("rate(x{service_name!=\"$service_name\"}[$__rate_interval])", vars));
    }

    [Fact]
    public void Interpolate_GleichOperatorBeiEinzelwert_BleibtGleich()
    {
        // Einzelwert (kein Regex) → = bleibt = (exakter Match, wie Grafana).
        var vars = new Dictionary<string, string> { ["service_name"] = "OtelSample" };
        Assert.Equal("rate(x{service_name=\"OtelSample\"}[$__rate_interval])",
            GrafanaTemplating.Interpolate("rate(x{service_name=\"$service_name\"}[$__rate_interval])", vars));
    }

    [Fact]
    public void Interpolate_RegexOperatorBleibtRegexOperator()
    {
        // Bereits =~ darf nicht doppelt befördert werden (kein =~~).
        var vars = new Dictionary<string, string> { ["host_name"] = "$__all" };
        Assert.Equal("rate(x{host_name=~\".*\"}[$__rate_interval])",
            GrafanaTemplating.Interpolate("rate(x{host_name=~\"$host_name\"}[$__rate_interval])", vars));
    }

    [Fact]
    public void Interpolate_OperatorBefoerderung_NurInGequotetemMatcher()
    {
        // Regex-Wert AUSSERHALB eines Matchers (z. B. topk($var, …)) darf den
        // Operator nicht anfassen — hier gibt es keinen =" davor.
        var vars = new Dictionary<string, string> { ["top"] = "$__all" };
        Assert.Equal("topk(.*, sum(x))",
            GrafanaTemplating.Interpolate("topk($top, sum(x))", vars));
    }

    [Fact]
    public void Interpolate_OperatorBefoerderung_MehrereMatcherGemischt()
    {
        // Realistischer otel-dotnet-webapi-Fall: = und =~ gemischt in einem Selector.
        var vars = new Dictionary<string, string>
        {
            ["service_name"] = "$__all",
            ["service_version"] = "$__all",
            ["deployment_environment"] = "$__all",
            ["host_name"] = "$__all",
            ["http_route"] = "$__all",
            ["http_response_status_code"] = "$__all",
            ["__rate_interval"] = "30s",
        };
        var expr = "rate(http_server_request_duration_seconds_count{" +
                   "service_name=\"$service_name\", service_version=\"$service_version\", " +
                   "deployment_environment=\"$deployment_environment\", host_name=~\"$host_name\", " +
                   "http_route=~\"$http_route\", http_response_status_code=~\"$http_response_status_code\"}" +
                   "[$__rate_interval])";
        var got = GrafanaTemplating.Interpolate(expr, vars);
        Assert.Equal("rate(http_server_request_duration_seconds_count{" +
                     "service_name=~\".*\", service_version=~\".*\", " +
                     "deployment_environment=~\".*\", host_name=~\".*\", " +
                     "http_route=~\".*\", http_response_status_code=~\".*\"}[30s])", got);
    }

    // --- DurationLabel -----------------------------------------------------

    [Theory]
    [InlineData(0, "1s")]
    [InlineData(1_000, "1s")]
    [InlineData(15_000, "15s")]
    [InlineData(59_999, "59s")]
    [InlineData(60_000, "1m")]
    [InlineData(120_000, "2m")]
    [InlineData(3_600_000, "1h")]
    [InlineData(7_200_000, "2h")]
    public void DurationLabel_Erwartet(long ms, string expected)
        => Assert.Equal(expected, GrafanaTemplating.DurationLabel(ms));

    // --- Encode ------------------------------------------------------------

    [Theory]
    [InlineData("$__all", ".*")]
    [InlineData("", ".*")]
    [InlineData("shop", "shop")]
    [InlineData("shop,billing", "shop|billing")]
    [InlineData("shop, billing", "shop|billing")]
    public void Encode_Erwartet(string input, string expected)
        => Assert.Equal(expected, GrafanaTemplating.Encode(input));

    // --- ParseLabelValuesLabel --------------------------------------------

    [Fact]
    public void ParseLabelValuesLabel_ExtrahiertLabel()
    {
        Assert.Equal("job", GrafanaTemplating.ParseLabelValuesLabel("label_values(http_requests_total, job)"));
        Assert.Equal("http_route",
            GrafanaTemplating.ParseLabelValuesLabel("label_values(http_requests_total{job=~\"$job\"}, http_route)"));
    }

    [Fact]
    public void ParseLabelValuesLabel_Ungueltig_LiefertNull()
    {
        Assert.Null(GrafanaTemplating.ParseLabelValuesLabel(null));
        Assert.Null(GrafanaTemplating.ParseLabelValuesLabel(""));
        Assert.Null(GrafanaTemplating.ParseLabelValuesLabel("metrics(http_requests_total, job)"));
        Assert.Null(GrafanaTemplating.ParseLabelValuesLabel("label_values(http_requests_total)"));
    }

    // --- ResolveOptions ----------------------------------------------------

    private sealed class FakeSource : IHeimdallMetricSource
    {
        public readonly List<HMetricPointView> Points = new();
        public IReadOnlyList<string> ListMetricNames(long? f = null, long? t = null)
            => new List<string> { "http_requests" };
        public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => new List<string> { "job", "http_route" };
        public IReadOnlyList<string> ListLabelValues(string n, IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => n switch { "job" => new List<string> { "billing", "shop" }, _ => new List<string>() };
        public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery q) => Points;
    }

    [Fact]
    public void ResolveOptions_Query_LiefertLabelValues()
    {
        var eng = new PromEngine(new FakeSource());
        var v = new GrafanaTemplatingVar("job", "query", "label_values(http_requests_total, job)", null, true, true);
        var opts = GrafanaTemplating.ResolveOptions(v, eng);
        Assert.Equal(new[] { "billing", "shop" }, opts.ToArray());
    }

    [Fact]
    public void ResolveOptions_Custom_LiefertKommaliste()
    {
        var eng = new PromEngine(new FakeSource());
        var v = new GrafanaTemplatingVar("env", "custom", "dev, prod, staging", null, false, false);
        var opts = GrafanaTemplating.ResolveOptions(v, eng);
        Assert.Equal(new[] { "dev", "prod", "staging" }, opts.ToArray());
    }

    [Fact]
    public void ResolveOptions_Datasource_LiefertLeer()
    {
        var eng = new PromEngine(new FakeSource());
        var v = new GrafanaTemplatingVar("DS", "datasource", "prometheus", null, false, false);
        Assert.Empty(GrafanaTemplating.ResolveOptions(v, eng));
    }

    // --- SelectedValue -----------------------------------------------------

    [Fact]
    public void SelectedValue_ExplizitSchlägtDefault()
    {
        var v = new GrafanaTemplatingVar("job", "query", "label_values(x, job)", "shop", true, true);
        var sel = new Dictionary<string, string> { ["job"] = "billing" };
        Assert.Equal("billing", GrafanaTemplating.SelectedValue(v, sel));
    }

    [Fact]
    public void SelectedValue_FehltNimmtCurrentOderAll()
    {
        var v = new GrafanaTemplatingVar("job", "query", "label_values(x, job)", "shop", true, true);
        Assert.Equal("shop", GrafanaTemplating.SelectedValue(v, null));
        Assert.Equal("shop", GrafanaTemplating.SelectedValue(v, new Dictionary<string, string>(0)));

        var v2 = new GrafanaTemplatingVar("job", "query", "label_values(x, job)", null, true, true);
        Assert.Equal("$__all", GrafanaTemplating.SelectedValue(v2, null));
    }
}