using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Heimdall.Prometheus;
using Xunit;

namespace Heimdall.Tests;

// ---------------------------------------------------------------------------
// PromQL-Evaluator- und Naming-Tests gegen einen In-Memory-FakeMetricSource
// (kein DB-Backend). Decken Phase 1/2/3 ab: Selektoren, rate/increase/irate
// mit Reset-Clamp, histogram_quantile, Aggregationen by/without, Vektor-
// Matching on(), Vergleiche mit bool, offset/@, *_over_time, Skalar-Funktionen,
// absent, sort, label_replace.
// ---------------------------------------------------------------------------

public class PromTests
{
    private const long S = 1_000_000_000L;   // 1 Sekunde in ns
    private const long Ms = 1_000_000L;      // 1 ms in ns

    private sealed class FakeSource : IHeimdallMetricSource
    {
        public readonly List<HMetricPointView> Points = new();

        public IReadOnlyList<string> ListMetricNames(long? fromUnixNano = null, long? toUnixNano = null)
            => Points.Select(p => p.Name).Distinct().OrderBy(n => n).ToArray();

        public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? matchers = null,
            long? fromUnixNano = null, long? toUnixNano = null)
            => Points.SelectMany(p => p.Labels.Keys).Distinct().OrderBy(k => k).ToArray();

        public IReadOnlyList<string> ListLabelValues(string labelName,
            IReadOnlyList<HLabelMatcher>? matchers = null, long? fromUnixNano = null, long? toUnixNano = null)
            => Points.Where(p => p.Labels.ContainsKey(labelName)).Select(p => p.Labels[labelName])
                     .Distinct().OrderBy(v => v).ToArray();

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

    private static FakeSource CounterSource()
    {
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "shop", ["region"] = "eu" };
        int[] vals = { 10, 21, 33, 46 };
        for (int i = 0; i < vals.Length; i++)
            src.Points.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
                i * S, vals[i], null, null, null, null, null, null, labels, "api"));
        return src;
    }

    private static PromEngine Engine(FakeSource src) => new(src);

    // --- Naming -------------------------------------------------------------
    [Fact]
    public void Mapper_Counter_Wird_TotalMitAlias()
    {
        var m = new MetricNameMapper();
        var names = m.CounterNames("orders", "1");
        Assert.Contains("orders_total", names);
        Assert.Contains("orders", names); // roher Alias
    }

    [Fact]
    public void Mapper_MsUnit_WirdSecondsUndSkaliert()
    {
        Assert.Equal("_seconds", MetricNameMapper.UnitSuffix("ms"));
        Assert.Equal(0.025, MetricNameMapper.ScaleValue("ms", 25));
        Assert.Equal("latency_seconds", new MetricNameMapper().PromBase("latency", "ms"));
    }

    [Fact]
    public void Mapper_HistogramDreiNamen()
    {
        var (b, sum, count) = new MetricNameMapper().HistogramNames("http.server.request.duration", "s");
        Assert.Equal("http_server_request_duration_seconds_bucket", b);
        Assert.Equal("http_server_request_duration_seconds_sum", sum);
        Assert.Equal("http_server_request_duration_seconds_count", count);
    }

    [Fact]
    public void Mapper_ServiceNameWirdJob()
    {
        Assert.Equal("job", MetricNameMapper.MapLabelKey("service.name"));
        Assert.Equal("http_route", MetricNameMapper.MapLabelKey("http.route"));
    }

    [Fact]
    public void Mapper_ServiceNameExponiertJobUndServiceName()
    {
        // service.name wird DOPPELT exponiert: job (klassisches Prom-Scrape-Label,
        // heimdall-overview) UND service_name (OTel-Collector-Konvention,
        // Community-Dashboards wie otel-dotnet-webapi). Sonstige Keys: 1:1.
        Assert.Equal(new[] { "job", "service_name" }, MetricNameMapper.MapLabelKeys("service.name"));
        Assert.Equal(new[] { "http_route" }, MetricNameMapper.MapLabelKeys("http.route"));
    }

    [Fact]
    public void Mapper_ResolvePromZuOtel_TotalUndBucket()
    {
        var m = new MetricNameMapper();
        var known = new[] { "orders", "http.server.request.duration" };
        Assert.Equal(new[] { "orders" }, m.ResolvePromToOtel("orders_total", known));
        Assert.Equal(new[] { "http.server.request.duration" }, m.ResolvePromToOtel("http_server_request_duration_seconds_bucket", known));
    }

    [Fact]
    public void Mapper_LegacyAlias_DotnetRuntimeWirdProcessNamen()
    {
        // .NET 9+ built-in System.Runtime emittiert `dotnet.*`; das Dashboard
        // otel-dotnet-webapi (gnetId 20568) fragt `process.*`/`process_runtime_dotnet_*`
        // ab. Der Legacy-Alias-Layer exponiert beides ADDITIV (s. MetricNameMapper).
        var m = new MetricNameMapper();
        // Counter: dotnet.exceptions -> process_runtime_dotnet_exceptions_count_total
        var counter = m.CounterNames("dotnet.exceptions", "{exception}");
        Assert.Contains("process_runtime_dotnet_exceptions_count_total", counter);
        Assert.Contains("dotnet_exceptions_total", counter);   // regulärer Name bleibt
        // Gauge: dotnet.thread_pool.thread.count -> process_runtime_dotnet_thread_pool_threads_count
        var gauge = m.GaugeNames("dotnet.thread_pool.thread.count", "{thread}");
        Assert.Contains("process_runtime_dotnet_thread_pool_threads_count", gauge);
        Assert.Contains("dotnet_thread_pool_thread_count", gauge);  // regulärer Name bleibt
        // Reverse-Richtung: Legacy-Prom-Name loest auf den OTel-Namen auf.
        var known = new[] { "dotnet.exceptions", "dotnet.thread_pool.thread.count" };
        Assert.Equal(new[] { "dotnet.exceptions" },
            m.ResolvePromToOtel("process_runtime_dotnet_exceptions_count_total", known));
        Assert.Equal(new[] { "dotnet.thread_pool.thread.count" },
            m.ResolvePromToOtel("process_runtime_dotnet_thread_pool_threads_count", known));
        // process_threads hat kein Gegenstück im built-in Meter -> bewusst NICHT aliasiert.
        Assert.Empty(m.ResolvePromToOtel("process_threads", known));
        // Metrik ohne Legacy-Alias -> LegacyAliases leer ( reguläre Namen unberührt).
        Assert.Empty(m.LegacyAliases("orders"));
    }

    [Fact]
    public void Eval_LegacyAlias_QueryProcessNamenTrifftDotnetMetrik()
    {
        // .NET 9+ built-in System.Runtime emittiert `dotnet.*` (Sum, kumulativ);
        // das Dashboard fragt `process_runtime_dotnet_*` ab. Der Legacy-Name muss
        // gegen die dotnet-Metrik aufloesen und den aktuellen Wert liefern.
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "OtelSample" };
        int[] vals = { 5, 5, 6, 6 };
        for (int i = 0; i < vals.Length; i++)
            src.Points.Add(new HMetricPointView("dotnet.thread_pool.thread.count", "{thread}",
                HMetricType.Sum, HTemporality.Cumulative, i * S, vals[i],
                null, null, null, null, null, null, labels, "Runtime"));
        var eng = Engine(src);

        var r = eng.EvalInstant("process_runtime_dotnet_thread_pool_threads_count", 3_000);
        Assert.Equal(PromResultKind.Vector, r.Kind);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.Equal(6, s.Value);
        Assert.Equal("process_runtime_dotnet_thread_pool_threads_count", s.Labels["__name__"]);
        Assert.Equal("OtelSample", s.Labels["service_name"]);

        // Der regulaere dotnet_*-Name bleibt abfragbar (Additivitaet).
        var r2 = eng.EvalInstant("dotnet_thread_pool_thread_count", 3_000);
        Assert.Equal(6, Assert.Single(r2.Vector!.Samples).Value);
    }

    // --- Selektor + Instant -------------------------------------------------
    [Fact]
    public void Eval_CounterSelektor_LiefertLetztenWert()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("orders_total", 3_000);
        Assert.Equal(PromResultKind.Vector, r.Kind);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.Equal(46, s.Value);
        Assert.Equal("shop", s.Labels["job"]);
        Assert.Equal("shop", s.Labels["service_name"]);   // service.name → job + service_name
        Assert.Equal("orders_total", s.Labels["__name__"]);
    }

    [Fact]
    public void Eval_RoherAlias_LiefertSelbenWert()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("orders", 3_000);
        Assert.Equal(46, Assert.Single(r.Vector!.Samples).Value);
    }

    [Fact]
    public void Eval_LabelMatcher_JobFilter()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("orders_total{job=\"shop\"}", 3_000);
        Assert.Single(r.Vector!.Samples);
        var r2 = eng.EvalInstant("orders_total{job=\"other\"}", 3_000);
        Assert.Empty(r2.Vector!.Samples);
    }

    [Fact]
    public void Eval_LabelMatcher_ServiceNameFilter_TrifftDoppeltesLabel()
    {
        // Community-Dashboards (otel-dotnet-webapi) filtern nach service_name, nicht
        // job. Da service.name beide Labels exponiert, muss {service_name=…} greifen
        // — und ~".*" darf Serie nicht ausschließen (Label ist vorhanden, nicht absent).
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("orders_total{service_name=\"shop\"}", 3_000);
        Assert.Single(r.Vector!.Samples);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.Equal("shop", s.Labels["service_name"]);
        Assert.Equal("shop", s.Labels["job"]);           // beide Labels vorhanden
        Assert.Empty(eng.EvalInstant("orders_total{service_name=\"other\"}", 3_000).Vector!.Samples);
        // ~".*" trifft, weil service_name jetzt existent ist (früher: absent → kein Match).
        Assert.Single(eng.EvalInstant("orders_total{service_name=~\".*\"}", 3_000).Vector!.Samples);
    }

    [Fact]
    public void Eval_SkalarLiteral()
    {
        var eng = Engine(new FakeSource());
        var r = eng.EvalInstant("3.5", 1_000);
        Assert.Equal(PromResultKind.Scalar, r.Kind);
        Assert.Equal(3.5, r.Scalar!.Value);
    }

    // --- rate / increase / irate -------------------------------------------
    [Fact]
    public void Eval_Rate_UeberDreiSekunden()
    {
        var eng = Engine(CounterSource());
        // Fenster [0,3]: Increase 46-10 = 36, range 3 s → rate = 12/s.
        var r = eng.EvalInstant("rate(orders_total[3s])", 3_000);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.True(Math.Abs(s.Value - 12) < 0.1, "rate=" + s.Value.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Eval_Increase_UeberFenster()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("increase(orders_total[3s])", 3_000);
        var s = Assert.Single(r.Vector!.Samples);
        // Fenster [0,3]: 46-10 = 36.
        Assert.True(Math.Abs(s.Value - 36) < 0.1, "increase=" + s.Value);
    }

    [Fact]
    public void Eval_Increase_CounterReset_AddiertPreResetWert()
    {
        // 100 → 30 (Reset): Prom nimmt an, der Counter resetete und stieg auf 30;
        // Increase = 30 (post-Reset), nicht negativ. rate bleibt >= 0.
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "shop" };
        src.Points.Add(Pt("c", 0, 100, labels));
        src.Points.Add(Pt("c", 1 * S, 30, labels));
        var eng = Engine(src);
        var inc = eng.EvalInstant("increase(c_total[1m])", 1_000);
        var s = Assert.Single(inc.Vector!.Samples);
        Assert.Equal(30, s.Value);
        var rate = eng.EvalInstant("rate(c_total[1m])", 1_000);
        Assert.True(Assert.Single(rate.Vector!.Samples).Value >= 0);
    }

    [Fact]
    public void Eval_Irate_LetztesDelta()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("irate(orders_total[1m])", 3_000);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.True(Math.Abs(s.Value - 13) < 0.1, "irate=" + s.Value);
    }

    // --- Aggregation --------------------------------------------------------
    [Fact]
    public void Eval_SumByJob()
    {
        var src = new FakeSource();
        var eu = new Dictionary<string, string> { ["service.name"] = "shop", ["region"] = "eu" };
        var us = new Dictionary<string, string> { ["service.name"] = "shop", ["region"] = "us" };
        src.Points.Add(Pt("orders", 0, 10, eu));
        src.Points.Add(Pt("orders", 0, 5, us));
        var eng = Engine(src);
        var r = eng.EvalInstant("sum by (job) (orders_total)", 1_000);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.Equal(15, s.Value);
        Assert.Equal("shop", s.Labels["job"]);
        Assert.False(s.Labels.ContainsKey("region"));
    }

    // --- Vektor-Matching + Vergleich ---------------------------------------
    [Fact]
    public void Eval_VergleichMitBool_LiefertNullEins()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("orders_total > bool 40", 3_000);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.Equal(1, s.Value);
    }

    [Fact]
    public void Eval_SkalarArithmetik()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("orders_total * 2", 3_000);
        Assert.Equal(92, Assert.Single(r.Vector!.Samples).Value);
    }

    // --- histogram_quantile -------------------------------------------------
    [Fact]
    public void Eval_HistogramQuantile_P95()
    {
        var src = new FakeSource();
        var labels = new Dictionary<string, string> { ["service.name"] = "shop" };
        double[] bounds = { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 };
        // p95 landet in Bucket 5 (le=0.25): 90 unterhalb, 10 in Bucket 5.
        var counts = new long[12];
        for (int b = 0; b < 5; b++) counts[b] = 18;
        counts[5] = 10;
        double sum = 0.5; // egal fuer Quantil
        src.Points.Add(new HMetricPointView("http.server.request.duration", "s", HMetricType.Histogram,
            HTemporality.Cumulative, 0, sum, 100, sum, 0, 0.25, counts, bounds, labels, "api"));
        var eng = Engine(src);
        var r = eng.EvalInstant("histogram_quantile(0.95, http_server_request_duration_seconds_bucket)", 1_000);
        var s = Assert.Single(r.Vector!.Samples);
        // 90/100 = 0.9 → oberes Ende Bucket 4 (le=0.1); 100/100=1 → +Inf. p95 im Bucket 5 (0.1..0.25).
        Assert.True(s.Value >= 0.1 && s.Value <= 0.25, "p95=" + s.Value.ToString(CultureInfo.InvariantCulture));
    }

    // --- *_over_time --------------------------------------------------------
    [Fact]
    public void Eval_AvgOverTime()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("avg_over_time(orders_total[3s])", 3_000);
        var s = Assert.Single(r.Vector!.Samples);
        // Werte 10,21,33,46 im Fenster [0,3] → avg = 27.5.
        Assert.True(Math.Abs(s.Value - 27.5) < 0.1, "avg=" + s.Value);
    }

    // --- Skalar-Funktionen --------------------------------------------------
    [Fact]
    public void Eval_AbsUndClampMin()
    {
        var eng = Engine(CounterSource());
        var neg = eng.EvalInstant("0 - orders_total", 3_000);
        Assert.Equal(-46, Assert.Single(neg.Vector!.Samples).Value);
        var abs = eng.EvalInstant("abs(0 - orders_total)", 3_000);
        Assert.Equal(46, Assert.Single(abs.Vector!.Samples).Value);
        var clamped = eng.EvalInstant("clamp_min(orders_total, 100)", 3_000);
        Assert.Equal(100, Assert.Single(clamped.Vector!.Samples).Value);
    }

    [Fact]
    public void Eval_Sort_LiefertAufsteigend()
    {
        var src = new FakeSource();
        var a = new Dictionary<string, string> { ["region"] = "a" };
        var b = new Dictionary<string, string> { ["region"] = "b" };
        src.Points.Add(Pt("orders", 0, 30, a));
        src.Points.Add(Pt("orders", 0, 10, b));
        var eng = Engine(src);
        var r = eng.EvalInstant("sort(orders_total)", 1_000);
        var vals = r.Vector!.Samples.Select(s => s.Value).ToArray();
        Assert.Equal(new double[] { 10, 30 }, vals);
    }

    [Fact]
    public void Eval_LabelReplace_SetztLabel()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("label_replace(orders_total, \"x\", \"val\", \"job\", \"(.*)\")", 3_000);
        var s = Assert.Single(r.Vector!.Samples);
        Assert.Equal("val", s.Labels["x"]);
    }

    [Fact]
    public void Eval_Absent_LeereSerie_LiefertEins()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalInstant("absent(orders_total{job=\"nope\"})", 3_000);
        Assert.Single(r.Vector!.Samples);
        Assert.Equal(1, Assert.Single(r.Vector!.Samples).Value);
    }

    // --- Range-Query --------------------------------------------------------
    [Fact]
    public void EvalRange_Rate_LiefertMatrix()
    {
        var eng = Engine(CounterSource());
        var r = eng.EvalRange("rate(orders_total[1m])", 0, 3_000, 1_000);
        Assert.Equal(PromResultKind.Matrix, r.Kind);
        var series = Assert.Single(r.Matrix!.Series);
        Assert.True(series.Points.Count >= 3);
    }

    // --- Discovery ----------------------------------------------------------
    [Fact]
    public void Discovery_ListMetricNames_EnthaeltTotal()
    {
        var eng = Engine(CounterSource());
        var names = eng.ListMetricNames();
        Assert.Contains("orders_total", names);
        Assert.Contains("orders", names);
    }

    [Fact]
    public void Discovery_ListLabelNames_ServiceNameWirdJobUndServiceName()
    {
        var eng = Engine(CounterSource());
        var names = eng.ListLabelNames();
        Assert.Contains("job", names);
        Assert.Contains("service_name", names);   // OTel-Collector-Konvention (zusätzlich)
        Assert.Contains("region", names);
        Assert.DoesNotContain("service.name", names);
    }

    [Fact]
    public void Discovery_ListLabelValues_Job()
    {
        var eng = Engine(CounterSource());
        var vals = eng.ListLabelValues("job");
        Assert.Equal(new[] { "shop" }, vals);
    }

    [Fact]
    public void Discovery_ListLabelValues_ServiceName()
    {
        // service_name stammt reverse-map aus service.name — gleiche Werte wie job.
        var eng = Engine(CounterSource());
        var vals = eng.ListLabelValues("service_name");
        Assert.Equal(new[] { "shop" }, vals);
    }

    [Fact]
    public void Discovery_ListLabelValues_Name_LiefertMetriknamen()
    {
        // __name__ ist ein Prom-Pseudo-Label: Werte = Metriknamen (nicht in OTel-
        // Attribut-Keys gespeichert). Vorher fiel __name__ durchs Raster und
        // /label/__name__/values lieferte leer — jetzt über ListMetricNames
        // (deckt auch die synthetisierten heimdall.*-Observability-Metriken ab).
        var eng = Engine(CounterSource());
        var vals = eng.ListLabelValues("__name__");
        Assert.Contains("orders_total", vals);
        Assert.Contains("orders", vals);   // roher Alias
    }

    [Fact]
    public void Discovery_Cache_LiefertKopie_UndKonsistenteErgebnisse()
    {
        // Hebel 2/3: Discovery wird 5 s gecacht. Der Cache gibt eine Kopie
        // zurueck — Caller-Mutation darf den naechsten Aufruf nicht korrumpieren.
        var eng = Engine(CounterSource());

        var first = eng.ListLabelValues("job");
        Assert.Equal(new[] { "shop" }, first);

        // Mutieren: der Cache darf davon nicht betroffen sein.
        ((List<string>)first).Add("corrupted");

        var second = eng.ListLabelValues("job");
        Assert.Equal(new[] { "shop" }, second);

        // Wiederholte Aufrufe liefern konsistente Ergebnisse (Cache-Hit).
        var names1 = eng.ListMetricNames();
        var names2 = eng.ListMetricNames();
        Assert.Equal(names1, names2);
    }

    [Fact]
    public void Discovery_Metadata_TypCounter()
    {
        var eng = Engine(CounterSource());
        var meta = eng.Metadata("orders_total");
        Assert.True(meta.ContainsKey("orders_total"));
        Assert.Equal("counter", meta["orders_total"][0].Type);
    }

    [Fact]
    public void Discovery_ListSeries_MatchSelektor()
    {
        var eng = Engine(CounterSource());
        // fromUnixNano=0 schliesst die Unix-1s-Saat ein (Default-Fenster waere 'now').
        var series = eng.ListSeries(new[] { "orders_total" }, 0, long.MaxValue);
        var s = Assert.Single(series);
        Assert.Equal("orders_total", s["__name__"]);
    }

    [Fact]
    public void BuildInfo_EnthaeltVersion()
    {
        var bi = PromEngine.BuildInfo();
        Assert.Equal("0.1.0", bi["version"]);
    }

    // --- Helfer -------------------------------------------------------------
    private static HMetricPointView Pt(string name, long tsNs, double value, Dictionary<string, string> labels)
        => new(name, "1", HMetricType.Sum, HTemporality.Cumulative, tsNs, value, null, null, null, null, null, null, labels, "api");
}