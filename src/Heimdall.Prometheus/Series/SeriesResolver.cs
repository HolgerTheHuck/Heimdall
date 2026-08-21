using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// IHeimdallMetricSource -> Prom-Serien.
//   - Expandiert jeden OTel-Punkt zu Prom-Samples (Counter: _total + Roalias;
//     Histogramm: _bucket{le} + _sum + _count; Gauge: name + Roalias).
//   - ms -> s Skalierung (Wert/Bounds), service.name -> job, '.' -> '_' in Labels.
//   - Delta-Temporalitaet: laufende Kumulierung je Serie (monoton fuer rate()).
//   - Offset/@-Modifier + 5m-Lookback fuer Instant-Selektion (letzter Punkt im
//     Fenster je Serie).
// Matcher aus der Abfrage (Prom-Label-Namen) werden IN-APP gegen die
// aufgebauten Prom-Labels gefiltert (inkl. __name__). FetchPoints bekommt keine
// Matcher — so vermeiden wir die mehrdeutige Rueck-Map _ -> '.'.
// ---------------------------------------------------------------------------

/// <summary>Ein expandierter Prom-Punkt (vor Lookback-Auswahl). Delta markiert
/// OTel-Delta-Punkte, die vor Exposition je Serie kumuliert werden.</summary>
internal sealed record PromSample(SeriesLabels Labels, long TimestampMs, double Value, bool Delta);

/// <summary>
/// Expandiert <see cref="IHeimdallMetricSource"/>-Punkte zu Prom-Serien (Alias- Namen,
/// Unit-Skalierung, Delta-Kumulierung, Offset/@-Modifier, 5-Minuten-Lookback). Siehe
/// Dateikommentar.
/// </summary>
internal sealed class SeriesResolver
{
    /// <summary>Default-Lookback fuer Instant-Selektion (5 Min, Prom-Standard).</summary>
    public const long DefaultLookbackMs = 300_000; // 5 Min (Prom-Default)

    private readonly IHeimdallMetricSource _source;
    private readonly MetricNameMapper _mapper;

    /// <summary>Erzeugt den Resolver ueber <paramref name="source"/> mit dem Namens-Mapper.</summary>
    public SeriesResolver(IHeimdallMetricSource source, MetricNameMapper mapper)
    { _source = source; _mapper = mapper; }

    /// <summary>Instant-Vektor fuer einen VectorSelector bei evalTime (ms).</summary>
    /// <summary>Instant-Vektor fuer einen VectorSelector bei evalTime (ms).
    /// <paramref name="preExpanded"/>: falls gesetzt (Range-Prefetch-Cache des
    /// Evaluators) wird daraus ein Zeit-Slice entnommen statt neu zu fetchen.</summary>
    public InstantVector ResolveInstant(VectorSelector vs, long evalTimeMs,
                                        long lookbackMs, long queryStartMs, long queryEndMs,
                                        IReadOnlyList<PromSample>? preExpanded = null)
    {
        var (fromMs, toMs) = WindowFor(vs, evalTimeMs, lookbackMs, queryStartMs, queryEndMs);
        var samples = preExpanded is not null ? SliceInPlace(preExpanded, fromMs, toMs) : FetchExpanded(vs, fromMs, toMs);
        if (samples.Count == 0) return InstantVector.Empty;

        // Letzten Punkt je Serie-Fingerprint mit ts <= effective_t selektieren.
        long effT = EffectiveTime(vs, evalTimeMs, queryStartMs, queryEndMs);
        var latest = new Dictionary<SeriesLabels, PromSample>();
        foreach (var s in samples)
        {
            if (s.TimestampMs > effT) continue;
            if (!latest.TryGetValue(s.Labels, out var cur) || s.TimestampMs > cur.TimestampMs)
                latest[s.Labels] = s;
        }
        var result = new List<Sample>(latest.Count);
        foreach (var kv in latest) result.Add(new Sample(kv.Key, kv.Value.TimestampMs, kv.Value.Value));
        return new InstantVector(result);
    }

    /// <summary>Range-Vektor (Matrix) fuer einen MatrixSelector bei evalTime (ms).
    /// <paramref name="preExpanded"/>: falls gesetzt (Range-Prefetch-Cache des
    /// Evaluators) wird daraus ein Zeit-Slice entnommen statt neu zu fetchen.</summary>
    public Matrix ResolveRange(MatrixSelector ms, long evalTimeMs,
                               long queryStartMs, long queryEndMs,
                               IReadOnlyList<PromSample>? preExpanded = null)
    {
        var vs = ms.Vector;
        long effT = EffectiveTime(vs, evalTimeMs, queryStartMs, queryEndMs);
        long fromMs = effT - ms.RangeMs;
        long toMs = effT;
        var samples = preExpanded is not null ? SliceInPlace(preExpanded, fromMs, toMs) : FetchExpanded(vs, fromMs, toMs);
        if (samples.Count == 0) return Matrix.Empty;

        var bySeries = new Dictionary<SeriesLabels, List<RangePoint>>();
        foreach (var s in samples)
        {
            if (!bySeries.TryGetValue(s.Labels, out var pts)) { pts = new List<RangePoint>(); bySeries[s.Labels] = pts; }
            pts.Add(new RangePoint(s.TimestampMs, s.Value));
        }
        var series = new List<RangeSeries>(bySeries.Count);
        foreach (var kv in bySeries)
        {
            kv.Value.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
            series.Add(new RangeSeries(kv.Key, kv.Value));
        }
        return new Matrix(series);
    }

    /// <summary>Distinct Prom-Labelsets (inkl. __name__) im Fenster — fuer /series.</summary>
    public IReadOnlyList<SeriesLabels> DiscoverSeries(VectorSelector vs, long fromMs, long toMs)
    {
        var samples = FetchExpanded(vs, fromMs, toMs);
        var set = new HashSet<SeriesLabels>();
        foreach (var s in samples) set.Add(s.Labels);
        return new List<SeriesLabels>(set);
    }

    // --- Fenster + effective time -----------------------------------------
    private (long fromMs, long toMs) WindowFor(VectorSelector vs, long evalTimeMs, long lookbackMs,
                                               long queryStartMs, long queryEndMs)
    {
        long effT = EffectiveTime(vs, evalTimeMs, queryStartMs, queryEndMs);
        return (effT - lookbackMs, effT);
    }

    private static long EffectiveTime(VectorSelector vs, long evalTimeMs, long queryStartMs, long queryEndMs)
    {
        if (vs.AtMs.HasValue)
        {
            // -1 = start()/end() — zur Laufzeit aufgeloest (Phase 2/3); hier auf evalTime.
            if (vs.AtMs.Value == -1) return evalTimeMs - vs.OffsetMs;
            return vs.AtMs.Value - vs.OffsetMs;
        }
        return evalTimeMs - vs.OffsetMs;
    }

    // --- Fetch + Expandieren + Delta-Kumulierung + Matcher-Filter ----------

    /// <summary>Zeit-Slice aus einem bereits expandierten Superset (Range-Prefetch):
    /// behält nur Proben mit <c>fromMs ≤ ts ≤ toMs</c>. Matcher-Filter und
    /// Delta-Kumulierung sind im Superset bereits enthalten — reines Filtern nach
    /// Zeitstempel genuegt und ist wert-identisch zum direkten Fetch des Fensters.</summary>
    private static List<PromSample> SliceInPlace(IReadOnlyList<PromSample> all, long fromMs, long toMs)
    {
        var result = new List<PromSample>(Math.Min(all.Count, 256));
        foreach (var s in all)
        {
            long ts = s.TimestampMs;
            if (ts >= fromMs && ts <= toMs) result.Add(s);
        }
        return result;
    }

    internal List<PromSample> FetchExpanded(VectorSelector vs, long fromMs, long toMs)
    {
        var otelNames = ResolveOtelNames(vs);
        if (otelNames.Count == 0) return new List<PromSample>();

        var q = new HMetricQuery(otelNames, null, MsToNs(fromMs), MsToNs(toMs));
        var points = _source.FetchPoints(q);
        if (points.Count == 0) return new List<PromSample>();

        // Nach (Serie) gruppieren, expandieren, ggf. Delta kumulieren, matchen.
        var expanded = new List<PromSample>(points.Count * 2);
        foreach (var p in points) expanded.AddRange(ExpandPoint(p));

        // Delta-Kumulierung je Fingerprint (monoton machen).
        var byFp = new Dictionary<SeriesLabels, List<PromSample>>();
        foreach (var s in expanded)
        {
            if (!byFp.TryGetValue(s.Labels, out var list)) { list = new List<PromSample>(); byFp[s.Labels] = list; }
            list.Add(s);
        }
        var result = new List<PromSample>(expanded.Count);
        foreach (var kv in byFp)
        {
            kv.Value.Sort((a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
            if (kv.Value.Count > 0 && kv.Value[0].Delta)
            {
                // Kumulierung nur fuer Delta-Counter/Histogram-Serien (nicht Gauge).
                double acc = 0;
                foreach (var s in kv.Value) { acc += s.Value; result.Add(s with { Value = acc, Delta = false }); }
            }
            else result.AddRange(kv.Value);
        }
        return ApplyMatchers(result, vs.Name, vs.Matchers);
    }

    private List<PromSample> ExpandPoint(HMetricPointView p)
    {
        var labels = BuildLabels(p.Labels);
        var samples = new List<PromSample>(8);
        long tsMs = p.TimeUnixNano / 1_000_000;

        switch (p.Type)
        {
            case HMetricType.Sum:
                {
                    var names = _mapper.CounterNames(p.Name, p.Unit);
                    double v = MetricNameMapper.ScaleValue(p.Unit, p.Value);
                    foreach (var n in names)
                        samples.Add(Make(n, labels, tsMs, v, p.Temporality == HTemporality.Delta));
                    break;
                }
            case HMetricType.Gauge:
                {
                    var names = _mapper.GaugeNames(p.Name, p.Unit);
                    double v = MetricNameMapper.ScaleValue(p.Unit, p.Value);
                    foreach (var n in names)
                        samples.Add(Make(n, labels, tsMs, v, false)); // Gauge nie kumulieren
                    break;
                }
            case HMetricType.Histogram:
                {
                    var (bN, sumN, countN) = _mapper.HistogramNames(p.Name, p.Unit);
                    var counts = p.BucketCounts;
                    var bounds = p.ExplicitBounds;
                    if (counts is not null && counts.Count > 0)
                    {
                        long cum = 0;
                        for (int i = 0; i < counts.Count; i++)
                        {
                            cum += counts[i];
                            double le = i < (bounds?.Count ?? 0)
                                ? MetricNameMapper.ScaleBound(p.Unit, bounds![i])
                                : double.PositiveInfinity;
                            var leLabels = labels.With("le", IsPosInf(le) ? "+Inf" : FormatDouble(le));
                            samples.Add(Make(bN, leLabels, tsMs, cum, p.Temporality == HTemporality.Delta));
                        }
                    }
                    double sum = MetricNameMapper.ScaleValue(p.Unit, p.Sum ?? 0);
                    samples.Add(Make(sumN, labels, tsMs, sum, p.Temporality == HTemporality.Delta));
                    samples.Add(Make(countN, labels, tsMs, p.Count ?? 0, p.Temporality == HTemporality.Delta));
                    break;
                }
        }
        return samples;

        static PromSample Make(string name, SeriesLabels baseLabels, long ts, double v, bool delta)
            => new PromSample(baseLabels.With("__name__", name), ts, v, delta);
    }

    private static SeriesLabels BuildLabels(IReadOnlyDictionary<string, string> raw)
    {
        // service.name wird doppelt exponiert (job + service_name, s. MapLabelKeys),
        // sonst 1:1 ('.' -> '_'). Beide Labels tragen denselben Wert, sodass
        // Prom-Selektoren beider Konventionen ({job=…} wie {service_name=…}) greifen.
        var dict = new Dictionary<string, string>(raw.Count + 1, StringComparer.Ordinal);
        foreach (var kv in raw)
            foreach (var k in MetricNameMapper.MapLabelKeys(kv.Key)) dict[k] = kv.Value;
        return new SeriesLabels(dict);
    }

    private List<PromSample> ApplyMatchers(List<PromSample> samples, string nameFilter, IReadOnlyList<Matcher> matchers)
    {
        bool filterName = !string.IsNullOrEmpty(nameFilter);
        bool hasMatchers = matchers is not null && matchers.Count > 0;
        if (!filterName && !hasMatchers) return samples;
        var result = new List<PromSample>(samples.Count);
        foreach (var s in samples)
        {
            // Bei explizitem Selektor-Namen nur die expandierte Serie mit passendem __name__
            // behalten — so fragt `orders_total` nur die _total-Alias-Serie ab und `orders`
            // nur den Roh-Alias (beide Aliase werden je Punkt expandiert).
            if (filterName && (!s.Labels.TryGetValue("__name__", out var nm) || nm != nameFilter)) continue;
            if (hasMatchers && matchers is not null)
            {
                bool ok = true;
                foreach (var m in matchers)
                {
                    s.Labels.TryGetValue(m.Name, out var v);
                    if (!MatchOne(v, m.Op, m.Value)) { ok = false; break; }
                }
                if (!ok) continue;
            }
            result.Add(s);
        }
        return result;
    }

    private static bool MatchOne(string? value, MatchOp op, string pattern)
    {
        switch (op)
        {
            case MatchOp.Eq: return string.Equals(value ?? string.Empty, pattern, StringComparison.Ordinal);
            case MatchOp.Ne: return !string.Equals(value ?? string.Empty, pattern, StringComparison.Ordinal);
            case MatchOp.Re: return value is not null && SafeRegex.IsMatch(value, pattern);
            case MatchOp.Nre: return value is null || !SafeRegex.IsMatch(value, pattern);
        }
        return false;
    }

    private List<string> ResolveOtelNames(VectorSelector vs)
    {
        var known = _source.ListMetricNames();
        if (string.IsNullOrEmpty(vs.Name))
        {
            // __name__-Matcher? dann dessen Werte als Prom-Namen behandeln.
            if (vs.Matchers is not null)
            {
                foreach (var m in vs.Matchers)
                    if (m.Name == "__name__" && m.Op == MatchOp.Eq)
                        return OtelFromPromName(m.Value, known);
            }
            return new List<string>(known);
        }
        return OtelFromPromName(vs.Name, known);
    }

    private List<string> OtelFromPromName(string promName, IReadOnlyList<string> known)
    {
        var resolved = _mapper.ResolvePromToOtel(promName, known);
        return resolved.Count > 0 ? new List<string>(resolved) : new List<string>();
    }

    private static long MsToNs(long ms) => ms * 1_000_000L;

    private static bool IsPosInf(double d) => double.IsPositiveInfinity(d);

    private static string FormatDouble(double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d))
            return d.ToString("0", CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}