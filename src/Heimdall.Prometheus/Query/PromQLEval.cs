using System;
using System.Collections.Generic;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// PromQL-Evaluator. Eval(node, timeMs) -> PromResult (Instant), EvalRange(node,
// start, end, step) -> PromResult (Matrix). Dispatch ueber PromNode-Typen;
// Funktionen/Aggregationen/Vektor-Matching in eigenen Klassen.
// ---------------------------------------------------------------------------

internal sealed class Evaluator
{
    public readonly SeriesResolver Resolver;
    public readonly long LookbackMs;
    public long QueryStartMs;
    public long QueryEndMs;

    public Evaluator(SeriesResolver resolver, long lookbackMs)
    { Resolver = resolver; LookbackMs = lookbackMs; }

    public PromResult Eval(PromNode node, long timeMs)
    {
        switch (node)
        {
            case NumberLiteral n: return PromResult.Of(new ScalarResult(n.Value, timeMs));
            case StringLiteral s: return PromResult.Of(new StringResult(s.Value, timeMs));
            case ParenExpr p: return Eval(p.Inner, timeMs);
            case VectorSelector vs:
                return PromResult.Of(Resolver.ResolveInstant(vs, timeMs, LookbackMs, QueryStartMs, QueryEndMs, LookupRangeCache(vs)));
            case MatrixSelector ms:
                return PromResult.Of(Resolver.ResolveRange(ms, timeMs, QueryStartMs, QueryEndMs, LookupRangeCache(ms.Vector)));
            case UnaryExpr u: return EvalUnary(u, timeMs);
            case BinaryExpr b: return PromQLBinary.Eval(b, this, timeMs);
            case AggregateExpr a: return PromQLAggregations.Invoke(a, this, timeMs);
            case FunctionCall f: return PromQLFunctions.Invoke(f, this, timeMs);
            default: throw new PromQLExecException("unsupported expression node: " + node.GetType().Name);
        }
    }

    /// <summary>Range-Auswertung: je Step Instant-Eval, zu Matrix gruppiert.</summary>
    public PromResult EvalRange(PromNode node, long startMs, long endMs, long stepMs)
    {
        QueryStartMs = startMs; QueryEndMs = endMs;
        if (stepMs <= 0) stepMs = 1;

        // Prefetch: pro VectorSelector EIN Superset-Fetch ueber das ganze Fenster
        // [start-lookback-maxRange, end] statt je Step einen Storage-Fetch
        // auszuloesen ( sonst N Fetches bei N Steps — Hauptkosten einer Range-Query).
        _rangeCache = new Dictionary<VectorSelector, List<PromSample>>();
        PrefetchSelectors(node, startMs, endMs);

        // Vektor-Ergebnis je Step → Matrix. Skalar-Ergebnis → Skalar-Serie (als Matrix mit einer {}-Serie).
        var bySeries = new Dictionary<SeriesLabels, List<RangePoint>>();
        var scalarPoints = new List<RangePoint>();
        PromResultKind? kind = null;

        for (long t = startMs; t <= endMs; t += stepMs)
        {
            PromResult r;
            try { r = Eval(node, t); }
            catch (PromQLExecException) { continue; } // Step ohne Daten ueberspringen

            if (r.Kind == PromResultKind.Scalar && r.Scalar is not null)
            {
                kind ??= PromResultKind.Scalar;
                scalarPoints.Add(new RangePoint(t, r.Scalar.Value));
            }
            else if (r.Kind == PromResultKind.Vector && r.Vector is not null)
            {
                kind ??= PromResultKind.Vector;
                foreach (var s in r.Vector.Samples)
                {
                    if (!bySeries.TryGetValue(s.Labels, out var pts)) { pts = new List<RangePoint>(); bySeries[s.Labels] = pts; }
                    pts.Add(new RangePoint(t, s.Value));
                }
            }
            else if (r.Kind == PromResultKind.Matrix)
                throw new PromQLExecException("range query requires instant-vector expression, got range vector");
        }

        if (kind == PromResultKind.Scalar)
        {
            var series = new List<RangeSeries> { new RangeSeries(SeriesLabels.Empty, scalarPoints) };
            return PromResult.Of(new Matrix(series));
        }
        var all = new List<RangeSeries>(bySeries.Count);
        foreach (var kv in bySeries) all.Add(new RangeSeries(kv.Key, kv.Value));
        return PromResult.Of(new Matrix(all));
    }

    // === Range-Prefetch ====================================================
    // Pro VectorSelector wird vor dem Step-Loop einmal das Superset-Fenster
    // geholt+expandiert; ResolveInstant/ResolveRange slicen daraus je Step.
    // Nur in EvalRange belegt (EvalInstant laesst _rangeCache null → Altverhalten).
    private Dictionary<VectorSelector, List<PromSample>>? _rangeCache;

    private List<PromSample>? LookupRangeCache(VectorSelector vs)
        => _rangeCache is not null && _rangeCache.TryGetValue(vs, out var list) ? list : null;

    /// <summary>Holt fuer jeden im AST vorkommenden VectorSelector einmal das
    /// Superset-Fenster [start−lookback−maxRange, end] — maxRange = maximale
    /// MatrixSelector-Range, die diesen Selektor umhüllt (0 bei reinem Instant-
    /// Selektor). Einmaliges Fetchen ersetzt N je-Step-Fetches.</summary>
    private void PrefetchSelectors(PromNode node, long startMs, long endMs)
    {
        var ranges = new Dictionary<VectorSelector, long>();
        CollectSelectors(node, ranges);
        if (ranges.Count == 0) return;
        foreach (var kv in ranges)
        {
            long lower = startMs - LookbackMs - kv.Value;
            if (lower < 0) lower = 0;
            _rangeCache![kv.Key] = Resolver.FetchExpanded(kv.Key, lower, endMs);
        }
    }

    /// <summary>Sammelt alle VectorSelector im AST mit der jeweiligen Max-RangeMs
    /// (0 fuer bare Instant-Selektoren). Vollstaendig ueber alle Knotentypen.</summary>
    private static void CollectSelectors(PromNode node, Dictionary<VectorSelector, long> ranges)
    {
        switch (node)
        {
            case VectorSelector vs:
                if (!ranges.ContainsKey(vs)) ranges[vs] = 0;
                break;
            case MatrixSelector ms:
                ranges[ms.Vector] = Math.Max(ranges.TryGetValue(ms.Vector, out var r) ? r : 0, ms.RangeMs);
                break;
            case ParenExpr p: CollectSelectors(p.Inner, ranges); break;
            case UnaryExpr u: CollectSelectors(u.Operand, ranges); break;
            case BinaryExpr b: CollectSelectors(b.Lhs, ranges); CollectSelectors(b.Rhs, ranges); break;
            case FunctionCall f: foreach (var a in f.Args) CollectSelectors(a, ranges); break;
            case AggregateExpr a: foreach (var arg in a.Args) CollectSelectors(arg, ranges); break;
        }
    }

    private PromResult EvalUnary(UnaryExpr u, long timeMs)
    {
        var inner = Eval(u.Operand, timeMs);
        if (inner.Kind == PromResultKind.Scalar && inner.Scalar is not null)
            return PromResult.Of(new ScalarResult(u.Op == BinOp.Sub ? -inner.Scalar.Value : inner.Scalar.Value, timeMs));
        if (inner.Kind == PromResultKind.Vector && inner.Vector is not null)
        {
            var samples = new List<Sample>(inner.Vector.Samples.Count);
            foreach (var s in inner.Vector.Samples)
                samples.Add(new Sample(s.Labels, s.TimestampMs, u.Op == BinOp.Sub ? -s.Value : s.Value));
            return PromResult.Of(new InstantVector(samples));
        }
        throw new PromQLExecException("unary operator requires scalar or vector");
    }
}

/// <summary>Eval-Ausnahme (wird als errorType „execution“ gemappt, HTTP 200).</summary>
public sealed class PromQLExecException : Exception
{
    /// <summary>Erzeugt die Ausnahme mit Meldung.</summary>
    public PromQLExecException(string message) : base(message) { }
}