using System;
using System.Collections.Generic;

namespace Heimdall.Blazor;

/// <summary>
/// Reine, werferfreie Helfer fuer die Ableitung von KPI-Zeitreihen aus rohen OTel-
/// Counter-Punkten. Bildet die Grundlage des Heimdall-Dashboards (calls/s, Errorrate,
/// Uptime, Spitzenlast) und arbeitet ausschließlich auf den bestehenden
/// <see cref="Heimdall.IHeimdallQuery.MetricSeries"/>-Punkten — keine Backend-Query,
/// kein Speicherpfad. Bewusst intern (via IVT fuer Tests sichtbar).
///
/// Alle Funktionen nehmen ihre Eingaben defensiv: leere oder einsprungige Serien
/// erzeugen keine Ausnahme, sondern leere bzw. Null-Ergebnisse. Counter-Resets
/// (Punkte mit kleinerem Wert als der Vorgänger) werden als Rate 0 interpretiert
/// (kennzeichnen einen Neustart des Counters), sofern <c>clampNegatives</c> gesetzt.
/// </summary>
internal static class HeimdallSeries
{
    private const double NanosPerSecond = 1_000_000_000.0;

    /// <summary>
    /// Projektion einer Metrik-Serie auf (Zeit, Wert)-Punkte. Reine Kopie der Werte —
    /// die Sortierung bleibt wie vom Backend geliefert (aufsteigend nach Zeit).
    /// </summary>
    public static IReadOnlyList<(long T, double V)> PointsFromMetric(IReadOnlyList<Heimdall.MetricRow> rows)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<(long, double)>();
        var pts = new List<(long T, double V)>(rows.Count);
        foreach (var r in rows)
            pts.Add((r.TimeUnixNano, r.Value));
        return pts;
    }

    /// <summary>
    /// Bildet eine kumulative (oder delta-) Wert-Serie auf eine Raten-Serie ab:
    /// <c>R[i] = (V[i] - V[i-1]) / ((T[i] - T[i-1]) / 1e9)</c> [pro Sekunde]. Der erste
    /// Punkt entfällt (kein Vorgänger). Zeitdifferenzen &lt; 1 ns werden ignoriert;
    /// negative Deltas (Counter-Reset) werden bei <paramref name="clampNegatives"/> auf
    /// 0 gesetzt. Liefert bei &lt; 2 Punkten eine leere Liste.
    /// </summary>
    public static IReadOnlyList<(long T, double R)> Rate(IReadOnlyList<(long T, double V)> pts, bool clampNegatives = true)
    {
        if (pts is null || pts.Count < 2) return Array.Empty<(long, double)>();
        var sorted = new List<(long T, double V)>(pts);
        sorted.Sort((a, b) => a.T.CompareTo(b.T));

        var result = new List<(long T, double R)>(sorted.Count - 1);
        for (int i = 1; i < sorted.Count; i++)
        {
            long dt = sorted[i].T - sorted[i - 1].T;
            if (dt < 1) continue;
            double dv = sorted[i].V - sorted[i - 1].V;
            if (clampNegatives && dv < 0) dv = 0;
            result.Add((sorted[i].T, dv / (dt / NanosPerSecond)));
        }
        return result;
    }

    /// <summary>
    /// Verhältnis zweier Raten-Serien (z.B. Fehler-Rate / Request-Rate) Punkt-für-Punkt
    /// über den gemeinsamen Indexbereich (min-Länge). <c>den == 0 → 0</c>; Ergebnis auf
    /// [0, 1] begrenzt. Leere Eingaben → leere Ausgabe.
    /// </summary>
    public static IReadOnlyList<(long T, double R)> ErrorRateSeries(
        IReadOnlyList<(long T, double R)> reqRate, IReadOnlyList<(long T, double R)> errRate)
    {
        if (reqRate is null || errRate is null) return Array.Empty<(long, double)>();
        int n = Math.Min(reqRate.Count, errRate.Count);
        if (n == 0) return Array.Empty<(long, double)>();
        var result = new List<(long T, double R)>(n);
        for (int i = 0; i < n; i++)
        {
            double den = reqRate[i].R;
            double ratio = den == 0 ? 0 : errRate[i].R / den;
            if (ratio < 0) ratio = 0;
            else if (ratio > 1) ratio = 1;
            result.Add((reqRate[i].T, ratio));
        }
        return result;
    }

    // ---------------------------------------------------------------------
    // KPI-Aggregate (werfen nie; leere Eingabe → 0)
    // ---------------------------------------------------------------------

    public static double Max(IReadOnlyList<(long T, double V)> pts)
    {
        if (pts is null || pts.Count == 0) return 0;
        double m = pts[0].V;
        for (int i = 1; i < pts.Count; i++) if (pts[i].V > m) m = pts[i].V;
        return m;
    }

    public static double Last(IReadOnlyList<(long T, double V)> pts)
    {
        if (pts is null || pts.Count == 0) return 0;
        return pts[pts.Count - 1].V;
    }

    public static double SumV(IReadOnlyList<(long T, double V)> pts)
    {
        if (pts is null || pts.Count == 0) return 0;
        double s = 0;
        for (int i = 0; i < pts.Count; i++) s += pts[i].V;
        return s;
    }

    public static double Mean(IReadOnlyList<(long T, double V)> pts)
    {
        if (pts is null || pts.Count == 0) return 0;
        return SumV(pts) / pts.Count;
    }

    /// <summary>
    /// Verfügbarkeit aus kumulativen Endwerten: <c>1 - totalErrors/totalRequests</c>,
    /// begrenzt auf [0, 1]. Leere Request-Serie oder 0-Anfragen → 1 (100 %). Fehler-
    /// Serie ohne Einträge → 1 (keine Fehler bekannt).
    /// </summary>
    public static double Uptime(IReadOnlyList<(long T, double V)> reqPts, IReadOnlyList<(long T, double V)> errPts)
    {
        if (reqPts is null || reqPts.Count == 0) return 1.0;
        double totalReq = Last(reqPts);
        if (totalReq <= 0) return 1.0;
        double totalErr = errPts is null || errPts.Count == 0 ? 0 : Last(errPts);
        double u = 1.0 - totalErr / totalReq;
        if (u < 0) u = 0;
        else if (u > 1) u = 1;
        return u;
    }

    // ---------------------------------------------------------------------
    // Histogramm-Quantile (Antwortzeiten p50/p95/p99)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>histogram_quantile</c> über Bucket-Counts und Explicit-Bounds mit linearer
    /// Interpolation im Ziel-Bucket. OTel-Konvention: <paramref name="counts"/> hat
    /// N+1 Einträge, <paramref name="bounds"/> N — Bucket i deckt
    /// <c>(bounds[i-1], bounds[i]]</c>; Bucket 0 <c>(-inf, bounds[0]]</c> (für Latenz
    /// als untere Schranke 0), letzter Bucket <c>(bounds[N-1], +inf]</c> → fällt das
    /// Quantil dorthin, wird die höchste endliche Bound geliefert (Prometheus-konform,
    /// kein +inf). Liefert 0 bei leerer Eingabe oder Summe 0. Werferfrei.
    /// </summary>
    public static double Quantile(IReadOnlyList<long> counts, IReadOnlyList<double> bounds, double q)
    {
        if (counts is null || counts.Count == 0 || bounds is null) return 0;
        double total = 0;
        for (int i = 0; i < counts.Count; i++) total += counts[i];
        if (total <= 0) return 0;

        double target = q * total;
        double running = 0;
        for (int i = 0; i < counts.Count; i++)
        {
            double prev = running;
            running += counts[i];
            if (running < target) continue;
            if (counts[i] <= 0) continue;

            // Schranken: Bucket 0 → 0..bounds[0]; Mitte → bounds[i-1]..bounds[i];
            // letzter (+Inf) → höchste endliche Bound (Interpolation entfällt).
            double lower = i == 0 ? 0 : bounds[Math.Min(i - 1, bounds.Count - 1)];
            double upper = i < bounds.Count ? bounds[i]
                                         : (bounds.Count > 0 ? bounds[bounds.Count - 1] : 0);
            double frac = (target - prev) / counts[i];
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            return lower + frac * (upper - lower);
        }
        // Alles im +Inf-Bucket → höchste endliche Bound.
        return bounds.Count > 0 ? bounds[bounds.Count - 1] : 0;
    }

    /// <summary>
    /// Quantil über sortierte Rohwerte (z. B. einzelne Span-Dauern), das Span-
    /// Gegenstück zu <see cref="Quantile"/> (das auf Histogramm-Buckets arbeitet).
    /// Lineare Interpolation zwischen den benachbarten Stichproben;
    /// <c>q=0</c> → Minimum, <c>q=1</c> → Maximum. Leere Eingabe oder Summe 0 → 0.
    /// Werferfrei. Der Aufrufer sortiert die Werte vorher (z. B. mit
    /// <see cref="AggregateDurations"/>).
    /// </summary>
    public static double QuantileValues(double[] sortedValues, double q)
    {
        if (sortedValues is null || sortedValues.Length == 0) return 0;
        int n = sortedValues.Length;
        if (n == 1) return sortedValues[0];
        if (q <= 0) return sortedValues[0];
        if (q >= 1) return sortedValues[n - 1];
        // R-6 (Microsoft Excel PERCENTILE.EXC-artig): Position (n+1)*q.
        double pos = (n + 1) * q;
        int lo = (int)Math.Floor(pos) - 1;     // 0-basierter Index der unteren Stichprobe
        if (lo < 0) return sortedValues[0];
        if (lo >= n - 1) return sortedValues[n - 1];
        double frac = pos - (lo + 1);
        return sortedValues[lo] + frac * (sortedValues[lo + 1] - sortedValues[lo]);
    }

    /// <summary>
    /// Sammelt die Dauern aller Spans in einer Gruppe, sortiert sie aufsteigend und
    /// liefert das fertige Array für <see cref="QuantileValues"/>. Leere Gruppe →
    /// leeres Array (dann liefert <c>QuantileValues</c> 0).
    /// </summary>
    public static double[] AggregateDurations(System.Collections.Generic.IReadOnlyList<Heimdall.SpanRow> spans)
    {
        if (spans is null || spans.Count == 0) return System.Array.Empty<double>();
        var ds = new double[spans.Count];
        for (int i = 0; i < spans.Count; i++) ds[i] = spans[i].DurationNs;
        System.Array.Sort(ds);
        return ds;
    }

    /// <summary>
    /// Pro Histogramm-Punkt das Quantil <paramref name="q"/> → (Zeit, Quantil)-Reihe.
    /// Delta-Temporalität (jeder Punkt = eigenes Fenster) wird direkt punktweise
    /// quantiliert; Cumulative-Punkte werden ebenfalls punktweise gelesen (MVP: das
    /// ist der jeweilige Stand, keine Fenster-Resampling — Folgeblock). Nicht-
    /// Histogramm-Punkte und leere Buckets entfallen. Leere Eingabe → leere Liste.
    /// </summary>
    public static IReadOnlyList<(long T, double V)> LatencySeries(IReadOnlyList<Heimdall.MetricRow> points, double q)
    {
        if (points is null || points.Count == 0) return Array.Empty<(long, double)>();
        var result = new List<(long T, double V)>(points.Count);
        foreach (var p in points)
        {
            if ((Heimdall.HMetricType)p.Type != Heimdall.HMetricType.Histogram) continue;
            var counts = HeimdallCharting.ParseLongs(p.BucketCountsJson);
            var bounds = HeimdallCharting.ParseDoubles(p.ExplicitBoundsJson);
            if (counts.Count == 0) continue;
            result.Add((p.TimeUnixNano, Quantile(counts, bounds, q)));
        }
        return result;
    }
}