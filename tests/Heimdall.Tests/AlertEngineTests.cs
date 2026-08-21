using System;
using System.Collections.Generic;
using System.Linq;
using Heimdall;
using Heimdall.Blazor.Alerts;
using Heimdall.Prometheus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Pure Unit-Tests fuer den AlertEvaluator: Zustandsautomat (Transition),
/// Bedingungsauswertung (EvalCondition) ueber alle 3 Signale und den vollen
/// ProcessRule-Zyklus (Pending→Firing→Resolved inkl. Dedup + Notify). Nutzt
/// Fake-Query/Fake-State-Store/Fake-Channel und eine echte PromEngine mit
/// Fake-MetricSource fuer den Metrik-Pfad. Kein HTTP-Stack, keine Platte.
/// </summary>
public class AlertEngineTests
{
    private static readonly long NowMs = 1_700_000_000_000L;      // fester Bezugspunkt
    private static readonly long NowNano = NowMs * 1_000_000L;

    private static AlertRule Rule(AlertSignal signal, int threshold = 5, long forSeconds = 10,
        string? promql = null, int? minSeverity = 17, bool? hasError = null,
        long windowSeconds = 300, string[]? channels = null) =>
        new("r1", "Test", true, signal, promql, null, minSeverity, hasError, null, null,
            windowSeconds, threshold, forSeconds, channels ?? new[] { "test" }, null, 0);

    private static AlertEvaluator NewEvaluator(IHeimdallQuery query, IAlertStateStore state,
        FakeChannel? channel = null, PromEngine? engine = null, IAlertRuleStore? ruleStore = null)
    {
        var channels = channel is null ? Enumerable.Empty<IAlertChannel>() : new[] { channel };
        return new AlertEvaluator(query, ruleStore ?? new InMemRuleStore(), state,
            channels, engine, NullLogger<AlertEvaluator>.Instance, new HeimdallAlertingOptions());
    }

    // === Zustandsautomat (pure) ===========================================
    [Fact]
    public void Transition_Ok_Firing_WirdPending()
    {
        var next = AlertEvaluator.Transition(Rule(AlertSignal.Log), null, true, 6, "m", NowMs, out var fire, out var resolve);
        Assert.Equal(AlertState.Pending, next.State);
        Assert.False(fire);
        Assert.False(resolve);
    }

    [Fact]
    public void Transition_Pending_Firing_NachFor_WirdFiringUndNotify()
    {
        var pending = new AlertEvent("r1", AlertState.Pending, NowMs - 20_000, null, 6, "m", NowMs - 20_000);
        var next = AlertEvaluator.Transition(Rule(AlertSignal.Log, forSeconds: 10), pending, true, 6, "m", NowMs, out var fire, out var resolve);
        Assert.Equal(AlertState.Firing, next.State);
        Assert.True(fire);
        Assert.False(resolve);
    }

    [Fact]
    public void Transition_Pending_Firing_VorFor_BleibtPending()
    {
        var pending = new AlertEvent("r1", AlertState.Pending, NowMs - 3_000, null, 6, "m", NowMs - 3_000);
        var next = AlertEvaluator.Transition(Rule(AlertSignal.Log, forSeconds: 10), pending, true, 6, "m", NowMs, out var fire, out var resolve);
        Assert.Equal(AlertState.Pending, next.State);
        Assert.False(fire);        // noch nicht lange genug → kein Notify
        Assert.False(resolve);
    }

    [Fact]
    public void Transition_Firing_Firing_BleibtFiring_OhneNotify()
    {
        var firing = new AlertEvent("r1", AlertState.Firing, NowMs - 20_000, NowMs - 20_000, 6, "m", NowMs - 1_000);
        var next = AlertEvaluator.Transition(Rule(AlertSignal.Log), firing, true, 6, "m", NowMs, out var fire, out var resolve);
        Assert.Equal(AlertState.Firing, next.State);
        Assert.False(fire);        // Dedup: kein Re-Notify
        Assert.False(resolve);
    }

    [Fact]
    public void Transition_Firing_NichtFiring_WirdResolvedMitNotify()
    {
        var firing = new AlertEvent("r1", AlertState.Firing, NowMs - 20_000, NowMs - 20_000, 6, "m", NowMs - 1_000);
        var next = AlertEvaluator.Transition(Rule(AlertSignal.Log), firing, false, 0, null, NowMs, out var fire, out var resolve);
        Assert.Equal(AlertState.Resolved, next.State);
        Assert.False(fire);
        Assert.True(resolve);      // vorher Firing gemeldet → Resolved-Notify
    }

    [Fact]
    public void Transition_Pending_NichtFiring_WirdOk_OhneNotify()
    {
        // Bedingung weggefallen, noch VOR Firing → still Ok (nie benachrichtigt).
        var pending = new AlertEvent("r1", AlertState.Pending, NowMs - 3_000, null, 6, "m", NowMs - 3_000);
        var next = AlertEvaluator.Transition(Rule(AlertSignal.Log), pending, false, 0, null, NowMs, out var fire, out var resolve);
        Assert.Equal(AlertState.Ok, next.State);
        Assert.False(fire);
        Assert.False(resolve);
    }

    // === EvalCondition pro Signal ========================================
    [Fact]
    public void EvalCondition_Log_FeuertBeiCountUeberSchwellen_LimitTrick()
    {
        var q = new FakeQuery { LogCount = 6 };
        var ev = NewEvaluator(q, new InMemStateStore());
        var (firing, value, _) = ev.EvalCondition(Rule(AlertSignal.Log, threshold: 5), NowMs, NowNano);
        Assert.True(firing);            // 6 > 5
        Assert.Equal(6, value);
    }

    [Fact]
    public void EvalCondition_Log_NichtFeuernBeiCountGleichSchwellen()
    {
        var q = new FakeQuery { LogCount = 5 };
        var ev = NewEvaluator(q, new InMemStateStore());
        var (firing, _, _) = ev.EvalCondition(Rule(AlertSignal.Log, threshold: 5), NowMs, NowNano);
        Assert.False(firing);           // 5 > 5 false (strikt größer)
    }

    [Fact]
    public void EvalCondition_Log_LimitKapptAufThresholdPlus1_AberFeuertTrotzdem()
    {
        // 100 Logs vorhanden, Threshold 5 → Limit=6 → SearchLogs liefert max 6 → 6 > 5 feuert.
        var q = new FakeQuery { LogCount = 100 };
        var ev = NewEvaluator(q, new InMemStateStore());
        var (firing, value, _) = ev.EvalCondition(Rule(AlertSignal.Log, threshold: 5), NowMs, NowNano);
        Assert.True(firing);
        Assert.Equal(6, value);         // Limit-Trick: nie mehr als threshold+1 geholt
    }

    [Fact]
    public void EvalCondition_Trace_FeuertBeiFehlerTracesUeberSchwellen()
    {
        var q = new FakeQuery { TraceCount = 3 };
        var ev = NewEvaluator(q, new InMemStateStore());
        var (firing, value, _) = ev.EvalCondition(Rule(AlertSignal.Trace, threshold: 2, hasError: true, minSeverity: null), NowMs, NowNano);
        Assert.True(firing);
        Assert.Equal(3, value);
    }

    [Fact]
    public void EvalCondition_Metric_FeuertBeiNichtLeeremVektor()
    {
        var src = new FakeMetricSource();
        src.Points.Add(new HMetricPointView("orders", "1", HMetricType.Sum, HTemporality.Cumulative,
            (NowMs - 1000) * 1_000_000L, 46, null, null, null, null, null, null,
            new Dictionary<string, string> { ["service.name"] = "shop" }, "api"));
        var engine = new PromEngine(src);
        var q = new FakeQuery();
        var ev = NewEvaluator(q, new InMemStateStore(), engine: engine);
        var (firing, value, _) = ev.EvalCondition(Rule(AlertSignal.Metric, promql: "orders_total > 0", minSeverity: null), NowMs, NowNano);
        Assert.True(firing);
        Assert.Equal(46, value);
    }

    [Fact]
    public void EvalCondition_Metric_PromEngineNull_WirdUebersprungen()
    {
        var q = new FakeQuery();
        var ev = NewEvaluator(q, new InMemStateStore(), engine: null);   // Prometheus deaktiviert
        var (firing, _, msg) = ev.EvalCondition(Rule(AlertSignal.Metric, promql: "orders_total > 0", minSeverity: null), NowMs, NowNano);
        Assert.False(firing);
        Assert.Contains("PromEngine", msg ?? "");
    }

    // === ProcessRule: voller Zyklus + Dedup + Disabled ====================
    [Fact]
    public async Task ProcessRule_Ok_Pending_Firing_Resolved_MitDedup()
    {
        var q = new FakeQuery { LogCount = 6 };
        var state = new InMemStateStore();
        var ch = new FakeChannel();
        var ev = NewEvaluator(q, state, ch);
        var rule = Rule(AlertSignal.Log, threshold: 5, forSeconds: 10);

        await ev.ProcessRule(rule, NowMs, NowNano);                         // Ok → Pending
        Assert.Equal(AlertState.Pending, state.Get("r1")?.State);
        Assert.Empty(ch.Sent);

        await ev.ProcessRule(rule, NowMs + 5_000, (NowMs + 5_000) * 1_000_000L);   // noch vor for
        Assert.Equal(AlertState.Pending, state.Get("r1")?.State);
        Assert.Empty(ch.Sent);

        await ev.ProcessRule(rule, NowMs + 11_000, (NowMs + 11_000) * 1_000_000L); // for abgelaufen → Firing + Notify
        Assert.Equal(AlertState.Firing, state.Get("r1")?.State);
        Assert.Single(ch.Sent);
        Assert.Equal(AlertState.Firing, ch.Sent[0].State);

        await ev.ProcessRule(rule, NowMs + 12_000, (NowMs + 12_000) * 1_000_000L); // weiter firing → Dedup, kein neuer Notify
        Assert.Equal(AlertState.Firing, state.Get("r1")?.State);
        Assert.Single(ch.Sent);

        q.LogCount = 0;                                                     // Bedingung weggefallen
        await ev.ProcessRule(rule, NowMs + 13_000, (NowMs + 13_000) * 1_000_000L); // → Resolved + Notify
        Assert.Equal(AlertState.Resolved, state.Get("r1")?.State);
        Assert.Equal(2, ch.Sent.Count);
        Assert.Equal(AlertState.Resolved, ch.Sent[1].State);
    }

    [Fact]
    public async Task ProcessRule_DeaktivierteRegel_WirdOkOhneNotify()
    {
        var q = new FakeQuery { LogCount = 6 };
        var state = new InMemStateStore();
        state.Put(new AlertEvent("r1", AlertState.Firing, NowMs, NowMs, 6, "m", NowMs));
        var ch = new FakeChannel();
        var ev = NewEvaluator(q, state, ch);
        var rule = Rule(AlertSignal.Log, threshold: 5) with { Enabled = false };

        await ev.ProcessRule(rule, NowMs, NowNano);
        Assert.Equal(AlertState.Ok, state.Get("r1")?.State);   // aufgeraeumt
        Assert.Empty(ch.Sent);                                  // kein Notify
    }

    // === Fakes ============================================================
    private sealed class FakeQuery : IHeimdallQuery
    {
        public int LogCount;
        public int TraceCount;
        public IReadOnlyList<LogRow> SearchLogs(LogSearch s) =>
            Enumerable.Range(0, Math.Min(LogCount, s.Limit < 1 ? 200 : s.Limit))
                .Select(i => new LogRow(NowNano - i * 1000, null, null, 17, "ERROR", "boom", "{}", "api"))
                .ToList();
        public IReadOnlyList<TraceSummary> ListTraces(TraceFilter f) =>
            Enumerable.Range(0, Math.Min(TraceCount, f.Limit < 1 ? 100 : f.Limit))
                .Select(i => new TraceSummary(Guid.NewGuid().ToString(), NowNano - i * 1000, NowNano, 1_000_000, 1, true))
                .ToList();
        public IReadOnlyList<SpanRow> GetTrace(string t) => Array.Empty<SpanRow>();
        public IReadOnlyList<SpanRow> ListSpans(SpanFilter f) => Array.Empty<SpanRow>();
        public IReadOnlyList<MetricRow> MetricSeries(string n, long? f, long? t, int lim = 500) => Array.Empty<MetricRow>();
        public long CountSpans() => 0;
        public long CountLogs() => LogCount;
        public long CountMetrics() => 0;
    }

    private sealed class FakeMetricSource : IHeimdallMetricSource
    {
        public readonly List<HMetricPointView> Points = new();
        public IReadOnlyList<string> ListMetricNames(long? f = null, long? t = null)
            => Points.Select(p => p.Name).Distinct().ToList();
        public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => Points.SelectMany(p => p.Labels.Keys).Distinct().ToList();
        public IReadOnlyList<string> ListLabelValues(string label, IReadOnlyList<HLabelMatcher>? m = null, long? f = null, long? t = null)
            => Points.Where(p => p.Labels.ContainsKey(label)).Select(p => p.Labels[label]).Distinct().ToList();
        public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery q)
        {
            var pts = Points.AsEnumerable();
            if (q.Names is { Count: > 0 } names) pts = pts.Where(p => names.Contains(p.Name));
            if (q.FromUnixNano.HasValue) pts = pts.Where(p => p.TimeUnixNano >= q.FromUnixNano.Value);
            if (q.ToUnixNano.HasValue) pts = pts.Where(p => p.TimeUnixNano <= q.ToUnixNano.Value);
            return pts.ToList();
        }
    }

    private sealed class FakeChannel : IAlertChannel
    {
        public string Name => "test";
        public readonly List<AlertNotification> Sent = new();
        public Task SendAsync(AlertNotification n, CancellationToken ct) { Sent.Add(n); return Task.CompletedTask; }
    }

    private sealed class InMemStateStore : IAlertStateStore
    {
        private readonly Dictionary<string, AlertEvent> _d = new(StringComparer.Ordinal);
        public AlertEvent? Get(string id) => _d.TryGetValue(id, out var e) ? e : null;
        public IReadOnlyDictionary<string, AlertEvent> All() => new Dictionary<string, AlertEvent>(_d);
        public void Put(AlertEvent ev) => _d[ev.RuleId] = ev;
        public void Remove(string id) => _d.Remove(id);
    }

    private sealed class InMemRuleStore : IAlertRuleStore
    {
        private readonly Dictionary<string, AlertRule> _d = new(StringComparer.Ordinal);
        public IReadOnlyList<AlertRuleRef> List() => _d.Values.Select(r => new AlertRuleRef(r.Id, r.Name, r.Signal, r.Enabled)).ToList();
        public AlertRule? Get(string id) => _d.TryGetValue(id, out var r) ? r : null;
        public string Save(AlertRule rule) { _d[rule.Id] = rule; return rule.Id; }
        public void Delete(string id) => _d.Remove(id);
    }
}