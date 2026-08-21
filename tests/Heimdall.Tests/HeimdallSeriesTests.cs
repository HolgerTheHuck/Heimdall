using System;
using System.Collections.Generic;
using Heimdall.Blazor;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Reine Helfer-Tests fuer die KPI-Ableitung (HeimdallSeries): Rate-Bildung, Counter-Reset,
/// ErrorRate-Paarung/Clamp, KPI-Aggregate und Uptime. Alle Eingaben werferfrei.
/// </summary>
public class HeimdallSeriesTests
{
    private const long S = 1_000_000_000L; // 1 Sekunde in ns

    // --- Rate ---------------------------------------------------------------

    [Fact]
    public void Rate_SteigendeKumulativeSerie_LiefertDeltasProSekunde()
    {
        // Werte 10,21,33,46 (Deltas 11,12,13), je 1 s Abstand.
        var pts = new List<(long, double)> { (0 * S, 10), (1 * S, 21), (2 * S, 33), (3 * S, 46) };
        var r = HeimdallSeries.Rate(pts);
        Assert.Equal(3, r.Count);
        Assert.Equal(11, r[0].R);
        Assert.Equal(12, r[1].R);
        Assert.Equal(13, r[2].R);
        Assert.Equal(1 * S, r[0].T);
    }

    [Fact]
    public void Rate_ZeitAuftaktWirdSkaliert()
    {
        // 2 s Abstand, Delta 10 → 5/s.
        var pts = new List<(long, double)> { (0, 0), (2 * S, 10) };
        var r = HeimdallSeries.Rate(pts);
        Assert.Single(r);
        Assert.Equal(5, r[0].R);
    }

    [Fact]
    public void Rate_NegativesDelta_CounterReset_WirdNull()
    {
        // Kumulativer Counter springt nach Neustart zurück → Rate 0.
        var pts = new List<(long, double)> { (0, 100), (1 * S, 30) };
        var r = HeimdallSeries.Rate(pts, clampNegatives: true);
        Assert.Single(r);
        Assert.Equal(0, r[0].R);
    }

    [Fact]
    public void Rate_OhneClamp_LaestNegativeZu()
    {
        var pts = new List<(long, double)> { (0, 100), (1 * S, 30) };
        var r = HeimdallSeries.Rate(pts, clampNegatives: false);
        Assert.Single(r);
        Assert.Equal(-70, r[0].R);
    }

    [Fact]
    public void Rate_ZuWenigPunkte_Leer()
    {
        Assert.Empty(HeimdallSeries.Rate(Array.Empty<(long, double)>()));
        Assert.Empty(HeimdallSeries.Rate(new List<(long, double)> { (0, 1) }));
        Assert.Empty(HeimdallSeries.Rate(null!));
    }

    [Fact]
    public void Rate_UnsortierteEingabe_WirdSortiert()
    {
        var pts = new List<(long, double)> { (2 * S, 30), (0, 10), (1 * S, 20) };
        var r = HeimdallSeries.Rate(pts);
        Assert.Equal(2, r.Count);
        Assert.Equal(10, r[0].R);
        Assert.Equal(10, r[1].R);
    }

    // --- ErrorRateSeries ----------------------------------------------------

    [Fact]
    public void ErrorRateSeries_PaarungUndBegrenzung()
    {
        var req = new List<(long, double)> { (1 * S, 10), (2 * S, 20), (3 * S, 5) };
        var err = new List<(long, double)> { (1 * S, 0), (2 * S, 5), (3 * S, 5) };
        var r = HeimdallSeries.ErrorRateSeries(req, err);
        Assert.Equal(3, r.Count);
        Assert.Equal(0, r[0].R);
        Assert.Equal(0.25, r[1].R);   // 5/20
        Assert.Equal(1.0, r[2].R);   // 5/5
    }

    [Fact]
    public void ErrorRateSeries_DenominatorNull_GibtNull()
    {
        var req = new List<(long, double)> { (1 * S, 0), (2 * S, 0) };
        var err = new List<(long, double)> { (1 * S, 3), (2 * S, 7) };
        var r = HeimdallSeries.ErrorRateSeries(req, err);
        Assert.All(r, x => Assert.Equal(0, x.R));
    }

    [Fact]
    public void ErrorRateSeries_ClampAufEins()
    {
        // mehr Fehler als Requests → begrenzt auf 1.
        var req = new List<(long, double)> { (1 * S, 2) };
        var err = new List<(long, double)> { (1 * S, 10) };
        var r = HeimdallSeries.ErrorRateSeries(req, err);
        Assert.Single(r);
        Assert.Equal(1.0, r[0].R);
    }

    [Fact]
    public void ErrorRateSeries_MinLaenge()
    {
        var req = new List<(long, double)> { (1 * S, 10), (2 * S, 10), (3 * S, 10) };
        var err = new List<(long, double)> { (1 * S, 1) };
        var r = HeimdallSeries.ErrorRateSeries(req, err);
        Assert.Single(r);
        Assert.Equal(0.1, r[0].R);
    }

    [Fact]
    public void ErrorRateSeries_Leer()
    {
        Assert.Empty(HeimdallSeries.ErrorRateSeries(
            Array.Empty<(long, double)>(), Array.Empty<(long, double)>()));
    }

    // --- KPI-Aggregate ------------------------------------------------------

    [Fact]
    public void Max_Last_Sum_Mean()
    {
        var pts = new List<(long, double)> { (0, 1), (1 * S, 3), (2 * S, 2) };
        Assert.Equal(3, HeimdallSeries.Max(pts));
        Assert.Equal(2, HeimdallSeries.Last(pts));
        Assert.Equal(6, HeimdallSeries.SumV(pts));
        Assert.Equal(2, HeimdallSeries.Mean(pts));
    }

    [Fact]
    public void Aggregate_Leer_GibtNull()
    {
        Assert.Equal(0, HeimdallSeries.Max(Array.Empty<(long, double)>()));
        Assert.Equal(0, HeimdallSeries.Last(Array.Empty<(long, double)>()));
        Assert.Equal(0, HeimdallSeries.SumV(Array.Empty<(long, double)>()));
        Assert.Equal(0, HeimdallSeries.Mean(Array.Empty<(long, double)>()));
    }

    // --- Uptime -------------------------------------------------------------

    [Fact]
    public void Uptime_AusKumulativenEndwerten()
    {
        var req = new List<(long, double)> { (0, 0), (1 * S, 200) };
        var err = new List<(long, double)> { (0, 0), (1 * S, 5) };   // 5 Fehler / 200 = 2,5 %
        Assert.Equal(0.975, HeimdallSeries.Uptime(req, err));
    }

    [Fact]
    public void Uptime_KeineFehler_GibtEins()
    {
        var req = new List<(long, double)> { (0, 0), (1 * S, 100) };
        Assert.Equal(1.0, HeimdallSeries.Uptime(req, Array.Empty<(long, double)>()));
    }

    [Fact]
    public void Uptime_KeineRequests_GibtEins()
    {
        Assert.Equal(1.0, HeimdallSeries.Uptime(Array.Empty<(long, double)>(), Array.Empty<(long, double)>()));
        var req = new List<(long, double)> { (0, 0) };   // totalReq 0
        Assert.Equal(1.0, HeimdallSeries.Uptime(req, new List<(long, double)> { (0, 5) }));
    }

    [Fact]
    public void Uptime_MehrFehlerAlsRequests_ClampNull()
    {
        var req = new List<(long, double)> { (0, 0), (1 * S, 10) };
        var err = new List<(long, double)> { (0, 0), (1 * S, 30) };
        Assert.Equal(0, HeimdallSeries.Uptime(req, err));
    }

    // --- PointsFromMetric ---------------------------------------------------

    [Fact]
    public void PointsFromMetric_ProjiziertZeitUndWert()
    {
        var rows = new List<Heimdall.MetricRow>
        {
            new("orders", "1", (int)Heimdall.HMetricType.Sum, (int)Heimdall.HTemporality.Cumulative,
                1_000_000_000L, 10.0, null, null, null, null, null, null, "{}"),
            new("orders", "1", (int)Heimdall.HMetricType.Sum, (int)Heimdall.HTemporality.Cumulative,
                2_000_000_000L, 21.0, null, null, null, null, null, null, "{}"),
        };
        var pts = HeimdallSeries.PointsFromMetric(rows);
        Assert.Equal(2, pts.Count);
        Assert.Equal(10.0, pts[0].V);
        Assert.Equal(21.0, pts[1].V);
    }

    [Fact]
    public void PointsFromMetric_Leer()
    {
        Assert.Empty(HeimdallSeries.PointsFromMetric(Array.Empty<Heimdall.MetricRow>()));
        Assert.Empty(HeimdallSeries.PointsFromMetric(null!));
    }

    // --- Quantile (histogram_quantile) --------------------------------------

    private static readonly double[] StdBounds =
        { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 };

    [Fact]
    public void Quantile_Seedform_LiefertP50P95P99()
    {
        // 30+30+30 in den unteren drei Buckets, 10 im p95-Bucket b3 (0.025..0.05].
        var counts = new long[] { 30, 30, 30, 10, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(0.0083333, HeimdallSeries.Quantile(counts, StdBounds, 0.50), 5);  // Bucket b1, Mitte+20/30
        Assert.Equal(0.0375,   HeimdallSeries.Quantile(counts, StdBounds, 0.95), 5);  // Bucket b3, Mitte
        Assert.Equal(0.0475,   HeimdallSeries.Quantile(counts, StdBounds, 0.99), 5);  // Bucket b3, 9/10
    }

    [Fact]
    public void Quantile_LeerOderSummeNull_GibtNull()
    {
        Assert.Equal(0, HeimdallSeries.Quantile(Array.Empty<long>(), StdBounds, 0.5));
        Assert.Equal(0, HeimdallSeries.Quantile(null!, StdBounds, 0.5));
        Assert.Equal(0, HeimdallSeries.Quantile(new long[] { 0, 0, 0, 0 }, new[] { 0.1, 0.2, 0.3 }, 0.5));
    }

    [Fact]
    public void Quantile_PlusInfBucket_GibtLetzteEndlicheBound()
    {
        // Alle Beobachtungen im +Inf-Bucket → p95 nicht interpolierbar → höchste Bound (10).
        var counts = new long[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 50 };
        Assert.Equal(10.0, HeimdallSeries.Quantile(counts, StdBounds, 0.95));
    }

    [Fact]
    public void Quantile_EinBucket_LiefertDessenMitte()
    {
        // 100 Beob. in b4 (0.05..0.1) → p50/95/99 alle dort, mitte = 0.075.
        var counts = new long[] { 0, 0, 0, 0, 100, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(0.075, HeimdallSeries.Quantile(counts, StdBounds, 0.50), 5);
    }

    // --- LatencySeries ------------------------------------------------------

    private static Heimdall.MetricRow HistRow(long tNs, string countsJson, string boundsJson) =>
        new("dur", "s", (int)Heimdall.HMetricType.Histogram, (int)Heimdall.HTemporality.Delta,
            tNs, 1.0, 100, 1.0, 0, 0.1, countsJson, boundsJson, "{}");

    [Fact]
    public void LatencySeries_QuantilProPunkt()
    {
        var rows = new List<Heimdall.MetricRow>
        {
            // Sekunde 1: p95 in b3 (0.025..0.05) → 0.0375 s
            HistRow(1_000_000_000L, "[30,30,30,10,0,0,0,0,0,0,0,0]", "[0.005,0.01,0.025,0.05,0.1,0.25,0.5,1,2.5,5,10]"),
            // Sekunde 2: 90 verteilt auf b0..b6 + 10 in b7 (0.5..1) → p95 = 0.75 s
            HistRow(2_000_000_000L, "[13,13,13,13,13,13,12,10,0,0,0,0]", "[0.005,0.01,0.025,0.05,0.1,0.25,0.5,1,2.5,5,10]"),
        };
        var p95 = HeimdallSeries.LatencySeries(rows, 0.95);
        Assert.Equal(2, p95.Count);
        Assert.Equal(0.0375, p95[0].V, 5);   // erste Sekunde ~38 ms
        Assert.Equal(0.75,   p95[1].V, 5);   // zweite Sekunde b7-Mitte (0.5..1)
        Assert.Equal(1_000_000_000L, p95[0].T);
    }

    [Fact]
    public void LatencySeries_NurHistogrammPunkte_UndLeer()
    {
        // Ein Sum-Punkt wird übersprungen, nur der Histogramm-Punkt kommt durch.
        var sumRow = new Heimdall.MetricRow("orders", "1", (int)Heimdall.HMetricType.Sum,
            (int)Heimdall.HTemporality.Cumulative, 1_000_000_000L, 10.0, null, null, null, null, null, null, "{}");
        var rows = new List<Heimdall.MetricRow> { sumRow, HistRow(2_000_000_000L, "[100,0,0]", "[0.05,0.1]") };
        var p50 = HeimdallSeries.LatencySeries(rows, 0.50);
        Assert.Single(p50);
        Assert.Equal(0.025, p50[0].V, 5);    // b0 (0..0.05) Mitte
        Assert.Empty(HeimdallSeries.LatencySeries(Array.Empty<Heimdall.MetricRow>(), 0.5));
        Assert.Empty(HeimdallSeries.LatencySeries(null!, 0.5));
    }
}