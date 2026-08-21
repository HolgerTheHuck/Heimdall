using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// PromQL-Funktionen. Range-Funktionen (rate/increase/irate/delta/*_over_time/
// absent_over_time) erwarten ein Matrix-Argument; Transform-Funktionen
// (abs/ceil/.../label_replace/label_join/sort/...) arbeiten auf Skalar/Vektor;
// histogram_quantile(q, bucketVector) interpoliert kumulierte _bucket-Serien.
// ---------------------------------------------------------------------------

internal static class PromQLFunctions
{
    public static PromResult Invoke(FunctionCall f, Evaluator ev, long timeMs)
    {
        string n = f.Name;
        if (_range.Contains(n)) return RangeFunc(n, f, ev, timeMs);
        if (n == "histogram_quantile" || n == "histogram_sum" || n == "histogram_count" || n == "histogram_avg")
            return HistogramFunc(n, f, ev, timeMs);
        if (n == "quantile_over_time") return OverTimeQuantile(f, ev, timeMs);
        if (n == "predict_linear") return PredictLinear(f, ev, timeMs);
        return Transform(n, f, ev, timeMs);
    }

    // === Range-Funktionen ===================================================
    private static readonly HashSet<string> _range = new(StringComparer.Ordinal)
    { "rate", "increase", "irate", "delta", "idelta", "deriv", "changes", "resets",
      "avg_over_time", "sum_over_time", "max_over_time", "min_over_time",
      "count_over_time", "last_over_time", "stddev_over_time", "stdvar_over_time",
      "present_over_time", "absent_over_time" };

    private static PromResult RangeFunc(string n, FunctionCall f, Evaluator ev, long timeMs)
    {
        if (f.Args.Count != 1) throw new PromQLExecException(n + " needs one range-vector arg");
        var matrix = RequireMatrix(ev.Eval(f.Args[0], timeMs));
        long rangeMs = MatrixRangeMs(f.Args[0]) ?? 0;
        double rangeSec = rangeMs / 1000.0;
        var outSamples = new List<Sample>(matrix.Series.Count);

        foreach (var rs in matrix.Series)
        {
            var pts = rs.Points;
            if (pts.Count == 0) continue;

            double v;
            switch (n)
            {
                case "rate": v = Rate(pts, rangeSec); break;
                case "increase": v = Increase(pts); break;
                case "delta": v = Increase(pts); break;
                case "irate": v = Irate(pts); break;
                case "idelta": v = pts.Count >= 2 ? pts[pts.Count - 1].Value - pts[pts.Count - 2].Value : pts[pts.Count - 1].Value; break;
                case "deriv": v = LinearSlope(pts); break;
                case "changes": v = Changes(pts); break;
                case "resets": v = Resets(pts); break;
                case "avg_over_time": v = Mean(pts); break;
                case "sum_over_time": v = Sum(pts); break;
                case "max_over_time": v = Max(pts); break;
                case "min_over_time": v = Min(pts); break;
                case "count_over_time": v = pts.Count; break;
                case "last_over_time": v = pts[pts.Count - 1].Value; break;
                case "present_over_time": v = pts.Count; break;
                case "stddev_over_time": v = Math.Sqrt(Var(pts)); break;
                case "stdvar_over_time": v = Var(pts); break;
                case "absent_over_time": continue; // unten behandelt
                default: throw new PromQLExecException("unknown range function: " + n);
            }
            outSamples.Add(new Sample(rs.Labels.WithoutName(), timeMs, v));
        }

        if (n == "absent_over_time")
            return PromResult.Of(matrix.Series.Count == 0
                ? new InstantVector(new[] { new Sample(SeriesLabels.Empty, timeMs, 1) })
                : InstantVector.Empty);

        return PromResult.Of(new InstantVector(outSamples));
    }

    // Counter-korrigierte Delta-Berechnung (Resets addieren den Wert vor Reset).
    private static double Increase(IReadOnlyList<RangePoint> pts)
    {
        double val = pts[pts.Count - 1].Value - pts[0].Value;
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].Value < pts[i - 1].Value) val += pts[i - 1].Value;
        return val;
    }
    private static double Rate(IReadOnlyList<RangePoint> pts, double rangeSec)
        => rangeSec > 0 ? Increase(pts) / rangeSec : double.NaN;
    private static double Irate(IReadOnlyList<RangePoint> pts)
    {
        if (pts.Count < 2) return double.NaN;
        var a = pts[pts.Count - 2]; var b = pts[pts.Count - 1];
        double d = b.Value - a.Value;
        if (d < 0) d = b.Value; // Reset
        double dt = (b.TimestampMs - a.TimestampMs) / 1000.0;
        return dt > 0 ? d / dt : double.NaN;
    }
    private static int Changes(IReadOnlyList<RangePoint> pts) { int c = 0; for (int i = 1; i < pts.Count; i++) if (pts[i].Value != pts[i - 1].Value) c++; return c; }
    private static int Resets(IReadOnlyList<RangePoint> pts) { int c = 0; for (int i = 1; i < pts.Count; i++) if (pts[i].Value < pts[i - 1].Value) c++; return c; }

    // === *_over_time Statistik =============================================
    private static double Sum(IReadOnlyList<RangePoint> p) { double s = 0; for (int i = 0; i < p.Count; i++) s += p[i].Value; return s; }
    private static double Mean(IReadOnlyList<RangePoint> p) => p.Count == 0 ? double.NaN : Sum(p) / p.Count;
    private static double Max(IReadOnlyList<RangePoint> p) { double m = double.NegativeInfinity; for (int i = 0; i < p.Count; i++) if (p[i].Value > m) m = p[i].Value; return m; }
    private static double Min(IReadOnlyList<RangePoint> p) { double m = double.PositiveInfinity; for (int i = 0; i < p.Count; i++) if (p[i].Value < m) m = p[i].Value; return m; }
    private static double Var(IReadOnlyList<RangePoint> p)
    { double m = Mean(p); double s = 0; for (int i = 0; i < p.Count; i++) { double d = p[i].Value - m; s += d * d; } return p.Count == 0 ? double.NaN : s / p.Count; }
    private static double LinearSlope(IReadOnlyList<RangePoint> p)
    {
        if (p.Count < 2) return 0;
        double n = p.Count; double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < p.Count; i++) { double x = p[i].TimestampMs / 1000.0; double y = p[i].Value; sx += x; sy += y; sxx += x * x; sxy += x * y; }
        double denom = n * sxx - sx * sx;
        return denom == 0 ? 0 : (n * sxy - sx * sy) / denom;
    }

    private static PromResult PredictLinear(FunctionCall f, Evaluator ev, long timeMs)
    {
        if (f.Args.Count != 2) throw new PromQLExecException("predict_linear needs (range-vector, scalar)");
        var matrix = RequireMatrix(ev.Eval(f.Args[0], timeMs));
        var sc = ev.Eval(f.Args[1], timeMs);
        if (sc.Kind != PromResultKind.Scalar || sc.Scalar is null) throw new PromQLExecException("predict_linear: second arg must be scalar");
        double dur = sc.Scalar.Value; // Sekunden in die Zukunft
        var outSamples = new List<Sample>();
        foreach (var rs in matrix.Series)
        {
            var pts = rs.Points; if (pts.Count < 2) continue;
            double slope = LinearSlope(pts);
            double intercept = Mean(pts) - slope * (pts.Sum(p => p.TimestampMs / 1000.0) / pts.Count);
            double predT = timeMs / 1000.0 + dur;
            outSamples.Add(new Sample(rs.Labels.WithoutName(), timeMs, slope * predT + intercept));
        }
        return PromResult.Of(new InstantVector(outSamples));
    }

    private static PromResult OverTimeQuantile(FunctionCall f, Evaluator ev, long timeMs)
    {
        if (f.Args.Count != 2) throw new PromQLExecException("quantile_over_time needs (scalar, range-vector)");
        var q = ev.Eval(f.Args[0], timeMs);
        if (q.Kind != PromResultKind.Scalar || q.Scalar is null) throw new PromQLExecException("quantile_over_time: q must be scalar");
        var matrix = RequireMatrix(ev.Eval(f.Args[1], timeMs));
        var outSamples = new List<Sample>();
        foreach (var rs in matrix.Series)
        {
            var vals = rs.Points.Select(p => p.Value).OrderBy(x => x).ToArray();
            outSamples.Add(new Sample(rs.Labels.WithoutName(), timeMs, QuantileOf(vals, q.Scalar.Value)));
        }
        return PromResult.Of(new InstantVector(outSamples));
    }

    // === histogram_quantile & Co. ==========================================
    private static PromResult HistogramFunc(string n, FunctionCall f, Evaluator ev, long timeMs)
    {
        if (f.Args.Count < 2) throw new PromQLExecException(n + " needs (scalar, vector)");
        var q = ev.Eval(f.Args[0], timeMs);
        if (q.Kind != PromResultKind.Scalar || q.Scalar is null) throw new PromQLExecException(n + ": first arg must be scalar");
        var v = RequireVector(ev.Eval(f.Args[1], timeMs));

        // Bucket-Samples nach Gruppe (alle Labels ausser le und __name__) ordnen.
        var groups = new Dictionary<SeriesLabels, List<(double le, double count)>>();
        foreach (var s in v.Samples)
        {
            if (!s.Labels.TryGetValue("le", out var leStr)) continue;
            double le = leStr == "+Inf" ? double.PositiveInfinity : double.Parse(leStr, CultureInfo.InvariantCulture);
            var key = s.Labels.Without("le").WithoutName();
            if (!groups.TryGetValue(key, out var list)) { list = new List<(double, double)>(); groups[key] = list; }
            list.Add((le, s.Value));
        }

        var outSamples = new List<Sample>(groups.Count);
        foreach (var kv in groups)
        {
            var buckets = kv.Value.OrderBy(b => b.le).ToArray();
            double total = buckets.Length > 0 ? buckets[buckets.Length - 1].count : 0;
            if (n == "histogram_count") { outSamples.Add(new Sample(kv.Key, timeMs, total)); continue; }
            // histogram_sum/avg benoetigen die _sum-Serie; hier nur _bucket-Serien vorhanden → NaN (Stub).
            if (n == "histogram_sum" || n == "histogram_avg") { outSamples.Add(new Sample(kv.Key, timeMs, double.NaN)); continue; }
            if (n == "histogram_avg") { outSamples.Add(new Sample(kv.Key, timeMs, total > 0 ? double.NaN : double.NaN)); continue; }
            // histogram_quantile:
            outSamples.Add(new Sample(kv.Key, timeMs, HistogramQuantile(buckets, q.Scalar.Value)));
        }
        return PromResult.Of(new InstantVector(outSamples));
    }

    private static double HistogramQuantile((double le, double count)[] buckets, double q)
    {
        if (buckets.Length == 0) return double.NaN;
        double total = buckets[buckets.Length - 1].count;
        if (total == 0 || double.IsNaN(q)) return double.NaN;
        if (q < 0) return double.NegativeInfinity;
        if (q > 1) return double.PositiveInfinity;
        double rank = q * total;
        if (rank <= buckets[0].count) return buckets[0].le == double.PositiveInfinity ? double.NaN : 0;
        for (int i = 1; i < buckets.Length; i++)
        {
            if (buckets[i].count >= rank)
            {
                double bLow = buckets[i - 1].count, bHigh = buckets[i].count;
                double leLow = buckets[i - 1].le, leHigh = buckets[i].le;
                if (leHigh == double.PositiveInfinity) return leLow; // Quantile im +Inf-Bucket → letzte finite Grenze
                if (bHigh == bLow) return leHigh;
                return leLow + (leHigh - leLow) * (rank - bLow) / (bHigh - bLow);
            }
        }
        return buckets[buckets.Length - 1].le;
    }

    // === Transform-Funktionen ==============================================
    private static PromResult Transform(string n, FunctionCall f, Evaluator ev, long timeMs)
    {
        // Skalarfunktionen ohne Vektor-Argument.
        if (n == "time") return PromResult.Of(new ScalarResult(timeMs / 1000.0, timeMs));
        if (n == "pi") return PromResult.Of(new ScalarResult(Math.PI, timeMs));
        if (n == "vector")
        {
            var a = ev.Eval(f.Args[0], timeMs);
            if (a.Kind != PromResultKind.Scalar || a.Scalar is null) throw new PromQLExecException("vector() needs a scalar");
            return PromResult.Of(new InstantVector(new[] { new Sample(SeriesLabels.Empty, timeMs, a.Scalar.Value) }));
        }
        if (n == "scalar")
        {
            var a = ev.Eval(f.Args[0], timeMs);
            if (a.Kind == PromResultKind.Scalar && a.Scalar is not null) return a;
            if (a.Kind == PromResultKind.Vector && a.Vector is not null)
            {
                if (a.Vector.Samples.Count != 1) throw new PromQLExecException("scalar() needs a 1-sample vector");
                return PromResult.Of(new ScalarResult(a.Vector.Samples[0].Value, timeMs));
            }
            return PromResult.Of(new ScalarResult(double.NaN, timeMs));
        }

        if (f.Args.Count == 0) throw new PromQLExecException(n + "() needs an argument");
        var arg = ev.Eval(f.Args[0], timeMs);

        if (n == "absent")
        {
            bool empty = arg.Kind == PromResultKind.Vector && (arg.Vector?.Samples.Count ?? 0) == 0;
            return PromResult.Of(empty ? new InstantVector(new[] { new Sample(SeriesLabels.Empty, timeMs, 1) }) : InstantVector.Empty);
        }
        if (n == "sort" || n == "sort_desc")
        {
            var v = RequireVector(arg);
            var sorted = n == "sort" ? v.Samples.OrderBy(s => s.Value).ToArray() : v.Samples.OrderByDescending(s => s.Value).ToArray();
            return PromResult.Of(new InstantVector(sorted));
        }
        if (n == "label_replace") return LabelReplace(arg, f, ev, timeMs);
        if (n == "label_join") return LabelJoin(arg, f, ev, timeMs);
        if (n == "timestamp")
        {
            var v = RequireVector(arg);
            var samples = new List<Sample>(v.Samples.Count);
            foreach (var s in v.Samples) samples.Add(new Sample(s.Labels, s.TimestampMs, s.TimestampMs / 1000.0));
            return PromResult.Of(new InstantVector(samples));
        }

        // Elementweise Skalar-/Vektor-Transform.
        if (arg.Kind == PromResultKind.Scalar && arg.Scalar is not null)
            return PromResult.Of(new ScalarResult(ApplyScalarFn(n, arg.Scalar.Value, null), timeMs));
        if (arg.Kind == PromResultKind.Vector && arg.Vector is not null)
        {
            double? p2 = f.Args.Count > 1 ? EvalNumber(ev, f.Args[1], timeMs) : null;
            var samples = new List<Sample>(arg.Vector.Samples.Count);
            foreach (var s in arg.Vector.Samples)
                samples.Add(new Sample(s.Labels, s.TimestampMs, ApplyScalarFn(n, s.Value, p2)));
            return PromResult.Of(new InstantVector(samples));
        }
        throw new PromQLExecException(n + ": unsupported argument type");
    }

    private static double ApplyScalarFn(string n, double v, double? p2)
    {
        switch (n)
        {
            case "abs": return Math.Abs(v);
            case "ceil": return Math.Ceiling(v);
            case "floor": return Math.Floor(v);
            case "round":
            {
                double rn = p2 ?? 1;
                return rn == 0 ? v : Math.Round(v / rn, MidpointRounding.AwayFromZero) * rn;
            }
            case "sqrt": return Math.Sqrt(v);
            case "exp": return Math.Exp(v);
            case "ln": return Math.Log(v);
            case "log2": return Math.Log2(v);
            case "log10": return Math.Log10(v);
            case "sgn": return v > 0 ? 1 : v < 0 ? -1 : 0;
            case "clamp_min": return Math.Max(v, p2 ?? double.NegativeInfinity);
            case "clamp_max": return Math.Min(v, p2 ?? double.PositiveInfinity);
            case "deg": return v * 180.0 / Math.PI;
            case "rad": return v * Math.PI / 180.0;
            case "sin": return Math.Sin(v); case "cos": return Math.Cos(v); case "tan": return Math.Tan(v);
            case "asin": return Math.Asin(v); case "acos": return Math.Acos(v); case "atan": return Math.Atan(v);
            case "sinh": return Math.Sinh(v); case "cosh": return Math.Cosh(v); case "tanh": return Math.Tanh(v);
            default: throw new PromQLExecException("unknown function: " + n);
        }
    }

    private static double? EvalNumber(Evaluator ev, PromNode node, long timeMs)
    {
        var r = ev.Eval(node, timeMs);
        return r.Kind == PromResultKind.Scalar && r.Scalar is not null ? r.Scalar.Value : null;
    }

    // label_replace(v, dst, repl, src, regex)
    private static PromResult LabelReplace(PromResult arg, FunctionCall f, Evaluator ev, long timeMs)
    {
        if (f.Args.Count != 5) throw new PromQLExecException("label_replace needs 5 args");
        var v = RequireVector(arg);
        string dst = StrArg(f, 1), repl = StrArg(f, 2), src = StrArg(f, 3), regex = StrArg(f, 4);
        var samples = new List<Sample>(v.Samples.Count);
        foreach (var s in v.Samples)
        {
            s.Labels.TryGetValue(src, out var srcVal);
            var m = SafeRegex.Match(srcVal ?? string.Empty, regex);
            var newLabels = s.Labels;
            if (m.Success)
            {
                // $N-Ersetzung: Prom label_replace ersetzt $1.. durch die Regex-Gruppen.
                string replaced = System.Text.RegularExpressions.Regex.Replace(repl, @"\$(\d)", mr =>
                {
                    int gi = int.Parse(mr.Groups[1].Value, CultureInfo.InvariantCulture);
                    return gi <= m.Groups.Count ? m.Groups[gi].Value : string.Empty;
                });
                newLabels = s.Labels.With(dst, replaced);
            }
            samples.Add(new Sample(newLabels, s.TimestampMs, s.Value));
        }
        return PromResult.Of(new InstantVector(samples));
    }

    // label_join(v, dst, sep, src...)
    private static PromResult LabelJoin(PromResult arg, FunctionCall f, Evaluator ev, long timeMs)
    {
        if (f.Args.Count < 3) throw new PromQLExecException("label_join needs (vector, dst, sep, src...)");
        var v = RequireVector(arg);
        string dst = StrArg(f, 1), sep = StrArg(f, 2);
        var srcs = new List<string>();
        for (int i = 3; i < f.Args.Count; i++) srcs.Add(StrArg(f, i));
        var samples = new List<Sample>(v.Samples.Count);
        foreach (var s in v.Samples)
        {
            var parts = new List<string>();
            foreach (var src in srcs) if (s.Labels.TryGetValue(src, out var sv)) parts.Add(sv);
            samples.Add(new Sample(s.Labels.With(dst, string.Join(sep, parts)), s.TimestampMs, s.Value));
        }
        return PromResult.Of(new InstantVector(samples));
    }

    private static string StrArg(FunctionCall f, int i)
    {
        if (f.Args[i] is StringLiteral sl) return sl.Value;
        throw new PromQLExecException("expected string argument");
    }

    // === Helfer =============================================================
    private static Matrix RequireMatrix(PromResult r)
    {
        if (r.Kind == PromResultKind.Matrix && r.Matrix is not null) return r.Matrix;
        throw new PromQLExecException("expected range vector (use [duration])");
    }
    private static InstantVector RequireVector(PromResult r)
    {
        if (r.Kind == PromResultKind.Vector && r.Vector is not null) return r.Vector;
        if (r.Kind == PromResultKind.Scalar && r.Scalar is not null)
            return new InstantVector(new[] { new Sample(SeriesLabels.Empty, r.Scalar.TimestampMs, r.Scalar.Value) });
        throw new PromQLExecException("expected instant vector");
    }
    private static long? MatrixRangeMs(PromNode node)
    {
        node = UnwrapParen(node);
        return node is MatrixSelector ms ? ms.RangeMs : null;
    }
    private static PromNode UnwrapParen(PromNode n)
    {
        while (n is ParenExpr p) n = p.Inner;
        return n;
    }
    private static double QuantileOf(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        if (q <= 0) return sorted[0];
        if (q >= 1) return sorted[sorted.Length - 1];
        double rank = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        double frac = rank - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}