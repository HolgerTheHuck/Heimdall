using System;
using System.Collections.Generic;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// Binaere PromQL-Operatoren: Arithmetik (+,-,*,/,%,^), Vergleiche
// (==,!=,>,<,>=,<=, mit bool) und Mengen (and,or,unless). Kombinationen:
// scalar/scalar, scalar/vector, vector/scalar, vector/vector (mit on/ignoring/
// group_left/right). Default-Match-Key = alle Labels ausser __name__.
// ---------------------------------------------------------------------------

internal static class PromQLBinary
{
    public static PromResult Eval(BinaryExpr b, Evaluator ev, long timeMs)
    {
        var l = ev.Eval(b.Lhs, timeMs);
        var r = ev.Eval(b.Rhs, timeMs);

        // Mengen-Operatoren nur vector/vector.
        if (b.Op == BinOp.And || b.Op == BinOp.Or || b.Op == BinOp.Unless)
            return SetOp(b.Op, AsVector(l), AsVector(r), b.Match);

        bool lScalar = l.Kind == PromResultKind.Scalar && l.Scalar is not null;
        bool rScalar = r.Kind == PromResultKind.Scalar && r.Scalar is not null;

        if (lScalar && rScalar)
        {
            double lv = l.Scalar!.Value;
            double rv = r.Scalar!.Value;
            if (IsCmp(b.Op)) return PromResult.Of(new ScalarResult(Compare(b.Op, lv, rv, b.Bool) ? 1 : 0, timeMs));
            return PromResult.Of(new ScalarResult(Arith(b.Op, lv, rv), timeMs));
        }
        if (lScalar && r.Vector is not null) return ScalarVector(b.Op, l.Scalar!.Value, r.Vector, true, b.Bool, timeMs);
        if (rScalar && l.Vector is not null) return ScalarVector(b.Op, r.Scalar!.Value, l.Vector, false, b.Bool, timeMs);

        if (l.Vector is not null && r.Vector is not null)
            return VectorVector(b.Op, l.Vector, r.Vector, b.Match, b.Bool, timeMs);

        throw new PromQLExecException("binary operator needs scalar or instant-vector operands");
    }

    // --- scalar OP vector (scalarOnRhs: rhs ist der Vektor bei true) ----------
    private static PromResult ScalarVector(BinOp op, double s, InstantVector v, bool scalarOnRhs, bool boolCmp, long t)
    {
        var samples = new List<Sample>(v.Samples.Count);
        foreach (var sm in v.Samples)
        {
            // scalarOnRhs = true: Skalar links, Vektor rechts (scalar OP vector).
            // scalarOnRhs = false: Vektor links, Skalar rechts (vector OP scalar).
            double lv = scalarOnRhs ? s : sm.Value;
            double rv = scalarOnRhs ? sm.Value : s;
            if (IsCmp(op))
            {
                bool keep = Compare(op, lv, rv, false);
                if (boolCmp) samples.Add(new Sample(sm.Labels, t, keep ? 1 : 0));
                else if (keep) samples.Add(sm);
            }
            else samples.Add(new Sample(sm.Labels, t, Arith(op, lv, rv)));
        }
        return PromResult.Of(new InstantVector(samples));
    }

    // --- vector OP vector ---------------------------------------------------
    private static PromResult VectorVector(BinOp op, InstantVector lv, InstantVector rv, VectorMatch? m, bool boolCmp, long t)
    {
        // Match-Key je Sample; rhs nach Key indizieren.
        var rhsByKey = new Dictionary<SeriesLabels, Sample>();
        foreach (var s in rv.Samples)
            rhsByKey[MatchKey(s.Labels, m)] = s;

        var samples = new List<Sample>(lv.Samples.Count);
        bool isCmp = IsCmp(op);

        foreach (var ls in lv.Samples)
        {
            var key = MatchKey(ls.Labels, m);
            if (!rhsByKey.TryGetValue(key, out var rs))
            {
                // many-to-one (group_left): lhs ist die viele-Seite → erst spaeter behandelt.
                continue;
            }
            if (isCmp)
            {
                bool keep = Compare(op, ls.Value, rs.Value, false);
                if (boolCmp) samples.Add(new Sample(ResultLabels(ls.Labels, m), t, keep ? 1 : 0));
                else if (keep) samples.Add(new Sample(ResultLabels(ls.Labels, m), ls.TimestampMs, ls.Value));
            }
            else
            {
                samples.Add(new Sample(ResultLabels(ls.Labels, m), t, Arith(op, ls.Value, rs.Value)));
            }
        }

        // group_left/right: viele-Seite ist die mit dem group-Vermerk.
        if (m is not null && (m.GroupSide == "left" || m.GroupSide == "right"))
            samples = ApplyGroup(op, lv, rv, m, boolCmp, t, samples);

        return PromResult.Of(new InstantVector(samples));
    }

    private static List<Sample> ApplyGroup(BinOp op, InstantVector lv, InstantVector rv, VectorMatch m, bool boolCmp, long t, List<Sample> oneToOne)
    {
        // group_left: lhs viele, rhs eine → fuer jeden lhs-Sample den passenden rhs (einer) finden.
        bool leftMany = m.GroupSide == "left";
        var many = leftMany ? lv.Samples : rv.Samples;
        var one = leftMany ? rv.Samples : lv.Samples;
        var oneByKey = new Dictionary<SeriesLabels, Sample>();
        foreach (var s in one) oneByKey[MatchKey(s.Labels, m)] = s;

        var samples = new List<Sample>(many.Count);
        bool isCmp = IsCmp(op);
        foreach (var ms in many)
        {
            if (!oneByKey.TryGetValue(MatchKey(ms.Labels, m), out var os)) continue;
            double lval = leftMany ? ms.Value : os.Value;
            double rval = leftMany ? os.Value : ms.Value;
            SeriesLabels outLabels = leftMany ? ResultLabels(ms.Labels, m) : ResultLabels(os.Labels, m);
            // include-Labels von der vielen-Seite uebernehmen.
            if (m.GroupLabels is not null)
                foreach (var il in m.GroupLabels)
                    if ((leftMany ? ms.Labels : os.Labels).TryGetValue(il, out var ilv))
                        outLabels = outLabels.With(il, ilv);
            if (isCmp)
            {
                bool keep = Compare(op, lval, rval, false);
                if (boolCmp) samples.Add(new Sample(outLabels, t, keep ? 1 : 0));
                else if (keep) samples.Add(new Sample(outLabels, t, leftMany ? lval : rval));
            }
            else samples.Add(new Sample(outLabels, t, Arith(op, lval, rval)));
        }
        return samples;
    }

    // --- Mengen-Operatoren (and = Schnitt, or = Vereinigung, unless = Differenz) --
    private static PromResult SetOp(BinOp op, InstantVector lv, InstantVector rv, VectorMatch? m)
    {
        var rhsKeys = new HashSet<SeriesLabels>();
        foreach (var s in rv.Samples) rhsKeys.Add(MatchKey(s.Labels, m));
        var samples = new List<Sample>(lv.Samples.Count);
        if (op == BinOp.And || op == BinOp.Unless)
        {
            foreach (var s in lv.Samples)
            {
                bool inR = rhsKeys.Contains(MatchKey(s.Labels, m));
                if (op == BinOp.And ? inR : !inR) samples.Add(s);
            }
        }
        else // Or: lhs + rhs (ohne lhs-Duplikate)
        {
            var seen = new HashSet<SeriesLabels>();
            foreach (var s in lv.Samples) { samples.Add(s); seen.Add(MatchKey(s.Labels, m)); }
            foreach (var s in rv.Samples) if (!seen.Contains(MatchKey(s.Labels, m))) samples.Add(s);
        }
        return PromResult.Of(new InstantVector(samples));
    }

    // --- Helfer -------------------------------------------------------------
    private static InstantVector AsVector(PromResult r)
    {
        if (r.Kind == PromResultKind.Vector && r.Vector is not null) return r.Vector;
        if (r.Kind == PromResultKind.Scalar && r.Scalar is not null)
            return new InstantVector(new[] { new Sample(SeriesLabels.Empty, r.Scalar.TimestampMs, r.Scalar.Value) });
        throw new PromQLExecException("set operator requires instant-vector");
    }

    private static SeriesLabels MatchKey(SeriesLabels labels, VectorMatch? m)
    {
        if (m is null) return labels.WithoutName();
        if (m.On) return labels.Project(m.Labels);
        return labels.WithoutNameAnd(m.Labels);
    }

    private static SeriesLabels ResultLabels(SeriesLabels lhs, VectorMatch? m)
    {
        if (m is null || !m.On) return lhs.WithoutName();
        // on: Ergebnis behaelt nur die on-Labels (plus __name__? Prom behaelt on-Labels, droppt andere).
        return lhs.Project(m.Labels);
    }

    internal static bool IsCmp(BinOp op) => op == BinOp.Eq || op == BinOp.Ne || op == BinOp.Gtr || op == BinOp.Lss || op == BinOp.Gte || op == BinOp.Lte;

    private static bool Compare(BinOp op, double l, double r, bool boolCmp)
    {
        bool res = op switch
        {
            BinOp.Eq => l == r, BinOp.Ne => l != r, BinOp.Gtr => l > r,
            BinOp.Lss => l < r, BinOp.Gte => l >= r, BinOp.Lte => l <= r, _ => false
        };
        return res;
    }

    private static double Arith(BinOp op, double l, double r) => op switch
    {
        BinOp.Add => l + r, BinOp.Sub => l - r, BinOp.Mul => l * r,
        BinOp.Div => l / r, BinOp.Mod => l % r, BinOp.Pow => Math.Pow(l, r), _ => double.NaN
    };
}