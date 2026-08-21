using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// PromQL-Aggregationen: sum/avg/max/min/count/group/stddev/stdvar (1 Arg),
// topk/bottomk (K, expr), quantile (q, expr), count_values (label, expr).
// Gruppierung per by(...)/without(...); Default = eine Gruppe ueber allem.
// Ergebnis-Labels = Gruppierungs-Labels (by) bzw. alle ausser without; __name__ entfaellt.
// ---------------------------------------------------------------------------

internal static class PromQLAggregations
{
    public static PromResult Invoke(AggregateExpr a, Evaluator ev, long timeMs)
    {
        string name = a.Name;
        InstantVector input;
        double param = 0;
        string? countLabel = null;

        if (name == "topk" || name == "bottomk" || name == "quantile")
        {
            if (a.Args.Count != 2) throw new PromQLExecException(name + " needs (scalar, vector)");
            var p = ev.Eval(a.Args[0], timeMs);
            if (p.Kind != PromResultKind.Scalar || p.Scalar is null) throw new PromQLExecException(name + ": first arg must be scalar");
            param = p.Scalar.Value;
            input = RequireVector(ev.Eval(a.Args[1], timeMs));
        }
        else if (name == "count_values")
        {
            if (a.Args.Count != 2) throw new PromQLExecException("count_values needs (string, vector)");
            var s = ev.Eval(a.Args[0], timeMs);
            if (s.Kind != PromResultKind.String || s.String is null) throw new PromQLExecException("count_values: first arg must be string");
            countLabel = s.String.Value;
            input = RequireVector(ev.Eval(a.Args[1], timeMs));
        }
        else
        {
            if (a.Args.Count != 1) throw new PromQLExecException(name + " needs one vector arg");
            input = RequireVector(ev.Eval(a.Args[0], timeMs));
        }

        if (name == "count_values" && countLabel is not null) return CountValues(input, countLabel, timeMs);

        // Gruppieren.
        var groups = new Dictionary<SeriesLabels, List<Sample>>();
        foreach (var s in input.Samples)
        {
            var key = GroupKey(s.Labels, a);
            if (!groups.TryGetValue(key, out var list)) { list = new List<Sample>(); groups[key] = list; }
            list.Add(s);
        }

        var outSamples = new List<Sample>(groups.Count);
        foreach (var kv in groups)
        {
            var vals = kv.Value;
            double res;
            switch (name)
            {
                case "sum": res = Sum(vals); break;
                case "avg": res = Sum(vals) / vals.Count; break;
                case "max": res = Max(vals); break;
                case "min": res = Min(vals); break;
                case "count": res = vals.Count; break;
                case "group": res = 1; break;
                case "stddev": res = StdDev(vals); break;
                case "stdvar": res = StdVar(vals); break;
                case "quantile": res = Quantile(vals, param); break;
                case "topk": AddTopK(outSamples, vals, (int)Math.Round(param), true, timeMs); continue;
                case "bottomk": AddTopK(outSamples, vals, (int)Math.Round(param), false, timeMs); continue;
                default: throw new PromQLExecException("unknown aggregation: " + name);
            }
            outSamples.Add(new Sample(kv.Key, timeMs, res));
        }
        return PromResult.Of(new InstantVector(outSamples));
    }

    private static PromResult CountValues(InstantVector input, string label, long timeMs)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in input.Samples)
            if (s.Labels.TryGetValue(label, out var v)) counts[v] = counts.TryGetValue(v, out var c) ? c + 1 : 1;
        var samples = new List<Sample>(counts.Count);
        foreach (var kv in counts)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal) { [label] = kv.Key };
            samples.Add(new Sample(new SeriesLabels(labels), timeMs, kv.Value));
        }
        return PromResult.Of(new InstantVector(samples));
    }

    private static void AddTopK(List<Sample> outSamples, List<Sample> vals, int k, bool top, long timeMs)
    {
        if (k <= 0) return;
        var ordered = top ? vals.OrderByDescending(s => s.Value) : vals.OrderBy(s => s.Value);
        int n = 0;
        foreach (var s in ordered) { if (n++ >= k) break; outSamples.Add(s); }
    }

    // --- Match-Key fuer Gruppierung ----------------------------------------
    private static SeriesLabels GroupKey(SeriesLabels labels, AggregateExpr a)
    {
        switch (a.Modifier)
        {
            case AggrModifier.By: return labels.Project(a.Labels);
            case AggrModifier.Without: return labels.WithoutNameAnd(a.Labels);
            default: return SeriesLabels.Empty; // alle in einer Gruppe
        }
    }

    private static InstantVector RequireVector(PromResult r)
    {
        if (r.Kind == PromResultKind.Vector && r.Vector is not null) return r.Vector;
        throw new PromQLExecException("aggregation requires instant-vector");
    }

    // --- Statistik-Helfer ---------------------------------------------------
    private static double Sum(List<Sample> v) { double s = 0; for (int i = 0; i < v.Count; i++) s += v[i].Value; return s; }
    private static double Max(List<Sample> v) { double m = double.NegativeInfinity; for (int i = 0; i < v.Count; i++) if (v[i].Value > m) m = v[i].Value; return m; }
    private static double Min(List<Sample> v) { double m = double.PositiveInfinity; for (int i = 0; i < v.Count; i++) if (v[i].Value < m) m = v[i].Value; return m; }
    private static double StdVar(List<Sample> v)
    { double mean = Sum(v) / v.Count; double s = 0; for (int i = 0; i < v.Count; i++) { double d = v[i].Value - mean; s += d * d; } return s / v.Count; }
    private static double StdDev(List<Sample> v) => Math.Sqrt(StdVar(v));

    private static double Quantile(List<Sample> v, double q)
    {
        if (v.Count == 0) return double.NaN;
        var sorted = v.Select(s => s.Value).OrderBy(x => x).ToArray();
        if (q <= 0) return sorted[0];
        if (q >= 1) return sorted[sorted.Length - 1];
        double rank = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        double frac = rank - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}