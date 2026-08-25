using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// PromEngine — Fassade ueber Parser + Evaluator + SeriesResolver + Discovery.
// Die HTTP-Endpunkte und DI greifen ausschliesslich hierauf zu. Zeiteinheiten
// nach aussen: Unix-Sekunden (float), intern Unix-Millisekunden (long).
// ---------------------------------------------------------------------------

/// <summary>
/// Fassade ueber Parser + Evaluator + SeriesResolver + Discovery. Die HTTP-Endpunkte
/// und DI greifen ausschliesslich hierauf zu. Zeiteinheiten nach aussen: Unix-Sekunden
/// (float), intern Unix-Millisekunden (long).
/// </summary>
public sealed class PromEngine
{
    private readonly IHeimdallMetricSource _source;
    private readonly MetricNameMapper _mapper;
    private readonly SeriesResolver _resolver;
    private readonly long _lookbackMs;

    /// <summary>Erzeugt die Engine ueber den storage-agnostischen <paramref name="source"/>.</summary>
    public PromEngine(IHeimdallMetricSource source, MetricNameMapper? mapper = null, long lookbackMs = SeriesResolver.DefaultLookbackMs)
    {
        _source = source;
        _mapper = mapper ?? new MetricNameMapper();
        _resolver = new SeriesResolver(source, _mapper);
        _lookbackMs = lookbackMs;
    }

    // --- Discovery-Cache (Hebel 2/3) ----------------------------------------
    // Kurzer TTL-Cache (5 s) fuer die Discovery-Methoden, die pro Dashboard-View
    // bzw. pro VectorSelector mehrfach aufgerufen werden (Template-Variablen,
    // __name__-Werte, ResolveOtelNames). Nur Discovery wird gecacht — FetchPoints
    // (PromQL-Auswertung) braucht frische Daten und bleibt ungecacht. Bei Hit wird
    // eine Kopie zurueckgegeben (Schutz vor Caller-Mutation).
    private const long DiscoveryCacheTtlMs = 5000;
    private const int DiscoveryCacheMax = 256;
    private readonly object _discoveryLock = new();
    private readonly Dictionary<string, (object Value, long ExpiresAtMs)> _discoveryCache = new(StringComparer.Ordinal);

    private IReadOnlyList<string> Cached(string key, Func<IReadOnlyList<string>> factory)
    {
        long now = Environment.TickCount64;
        lock (_discoveryLock)
        {
            if (_discoveryCache.TryGetValue(key, out var e) && e.ExpiresAtMs > now)
                return new List<string>((IReadOnlyList<string>)e.Value);
        }
        var value = factory();
        lock (_discoveryLock)
        {
            if (_discoveryCache.Count >= DiscoveryCacheMax)
            {
                // Abgelaufene evicten; reicht das nicht, komplett leeren (einfach, selten).
                var expired = _discoveryCache.Where(kv => kv.Value.ExpiresAtMs <= now).Select(kv => kv.Key).ToList();
                foreach (var k in expired) _discoveryCache.Remove(k);
                if (_discoveryCache.Count >= DiscoveryCacheMax) _discoveryCache.Clear();
            }
            _discoveryCache[key] = (value, now + DiscoveryCacheTtlMs);
        }
        // Auch beim Miss eine Kopie zurueckgeben — der Caller darf die gecachte
        // Liste nicht mutieren (sonst korrumpiert er den naechsten Cache-Hit).
        return new List<string>(value);
    }

    // === Auswertung ========================================================
    /// <summary>Wertet <paramref name="query"/> an einem Zeitpunkt (ms) aus (Prom <c>/api/v1/query</c>).</summary>
    public PromResult EvalInstant(string query, long timeMs)
    {
        var node = Parser.Parse(query);
        var ev = new Evaluator(_resolver, _lookbackMs) { QueryStartMs = timeMs, QueryEndMs = timeMs };
        return ev.Eval(node, timeMs);
    }

    /// <summary>Wertet <paramref name="query"/> ueber [start,end] mit Step (ms) aus (Prom <c>/query_range</c>).</summary>
    public PromResult EvalRange(string query, long startMs, long endMs, long stepMs)
    {
        var node = Parser.Parse(query);
        var ev = new Evaluator(_resolver, _lookbackMs);
        return ev.EvalRange(node, startMs, endMs, stepMs);
    }

    // === Discovery =========================================================
    /// <summary>Prom-Metriknamen im Fenster (Counter→_total, Histogramm→Familie, Gauge→name).</summary>
    public IReadOnlyList<string> ListMetricNames(long? fromUnixNano = null, long? toUnixNano = null)
        => Cached("names|" + fromUnixNano + "|" + toUnixNano, () =>
        {
            var otelNames = _source.ListMetricNames(fromUnixNano, toUnixNano);
            var prom = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var otel in otelNames)
            {
                var pts = _source.FetchPoints(new HMetricQuery(new[] { otel }, null, fromUnixNano, toUnixNano, 1));
                if (pts.Count == 0) { prom.Add(_mapper.PromBase(otel, null)); continue; }
                var p = pts[0];
                switch (p.Type)
                {
                    case HMetricType.Sum: foreach (var n in _mapper.CounterNames(otel, p.Unit)) prom.Add(n); break;
                    case HMetricType.Histogram:
                        var (b, s, c) = _mapper.HistogramNames(otel, p.Unit);
                        prom.Add(b); prom.Add(s); prom.Add(c); break;
                    default: foreach (var n in _mapper.GaugeNames(otel, p.Unit)) prom.Add(n); break;
                }
            }
            return new List<string>(prom);
        });

    /// <summary>Prom-Label-Namen (service.name→job+service_name, '.'→'_'), roh aus dem Source gemappt.</summary>
    public IReadOnlyList<string> ListLabelNames(long? fromUnixNano = null, long? toUnixNano = null)
        => Cached("labelnames|" + fromUnixNano + "|" + toUnixNano, () =>
        {
            var raw = _source.ListLabelNames(null, fromUnixNano, toUnixNano);
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in raw)
                foreach (var p in MetricNameMapper.MapLabelKeys(k)) set.Add(p);
            return new List<string>(set);
        });

    /// <summary>Werte eines Prom-Labels (reverse-map prom→OTel-Keys, Werte vereinigt).</summary>
    public IReadOnlyList<string> ListLabelValues(string promLabel, long? fromUnixNano = null, long? toUnixNano = null)
        => Cached("labelvalues|" + promLabel + "|" + fromUnixNano + "|" + toUnixNano, () =>
        {
            // __name__ ist ein Prom-Pseudo-Label: seine Werte sind die Metriknamen,
            // nicht in OTel-Attribut-Keys gespeichert (kein OTel-Key mappt auf __name__,
            // darum fiel es bisher durchs Raster und /label/__name__/values war leer).
            // Prom-konform über ListMetricNames liefern — deckt echte OTel-Metriken
            // UND die synthetisierten heimdall.*-Observability-Metriken (A4) ab.
            if (promLabel == "__name__")
                return ListMetricNames(fromUnixNano, toUnixNano);

            var rawNames = _source.ListLabelNames(null, fromUnixNano, toUnixNano);
            // Reverse-Map: ein Prom-Label kann aus mehreren OTel-Keys stammen (job/service_name
            // beide aus service.name). Werte aller treffenden OTel-Keys werden vereinigt.
            var otelKeys = rawNames.Where(k => MetricNameMapper.MapLabelKeys(k).Contains(promLabel)).ToList();
            var values = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in otelKeys)
                foreach (var v in _source.ListLabelValues(k, null, fromUnixNano, toUnixNano)) values.Add(v);
            return new List<string>(values);
        });

    /// <summary>Serien (Labelsets inkl. __name__) passend zu match[]-Selektoren im Fenster.</summary>
    public IReadOnlyList<SeriesLabels> ListSeries(IReadOnlyList<string> matchSelectors, long? fromUnixNano = null, long? toUnixNano = null)
    {
        var all = new HashSet<SeriesLabels>();
        long fromMs = fromUnixNano.HasValue ? fromUnixNano.Value / 1_000_000 : (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lookbackMs);
        long toMs = toUnixNano.HasValue ? toUnixNano.Value / 1_000_000 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var sel in matchSelectors)
        {
            var node = Parser.Parse(sel);
            if (node is VectorSelector vs)
                foreach (var l in _resolver.DiscoverSeries(vs, fromMs, toMs)) all.Add(l);
        }
        return new List<SeriesLabels>(all);
    }

    /// <summary>Metadaten (type) fuer einen Prom-Metriknamen.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MetricMeta>> Metadata(string? metric)
    {
        var result = new Dictionary<string, IReadOnlyList<MetricMeta>>(StringComparer.Ordinal);
        foreach (var otel in _source.ListMetricNames())
        {
            var pts = _source.FetchPoints(new HMetricQuery(new[] { otel }, null, null, null, 1));
            if (pts.Count == 0) continue;
            var p = pts[0];
            var names = p.Type switch
            {
                HMetricType.Sum => _mapper.CounterNames(otel, p.Unit),
                HMetricType.Histogram => new[] { _mapper.HistogramNames(otel, p.Unit).bucket, _mapper.HistogramNames(otel, p.Unit).sum, _mapper.HistogramNames(otel, p.Unit).count },
                _ => _mapper.GaugeNames(otel, p.Unit)
            };
            string type = p.Type switch
            {
                HMetricType.Sum => "counter",
                HMetricType.Histogram => "histogram",
                _ => "gauge"
            };
            var meta = new[] { new MetricMeta(type, string.Empty, string.Empty) };
            foreach (var n in names) if (metric is null || n == metric) result[n] = meta;
        }
        return result;
    }

    /// <summary>Buildinfo (Grafana fragt /status/buildinfo beim Verbinden ab).</summary>
    public static IReadOnlyDictionary<string, string> BuildInfo() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["version"] = "0.1.0", ["revision"] = "heimdall", ["branch"] = string.Empty,
        ["buildUser"] = "heimdall", ["buildDate"] = string.Empty, ["goVersion"] = string.Empty
    };

    /// <summary>Prometheus-Text-Exposition (<c>/api/v1/metrics</c>): je Prom-Metrikname
    /// ein <c># TYPE</c>-Header plus die aktuellen Serien (Letzter Punkt je Serie im
    /// Lookback-Fenster). Histogram-Familien teilen sich ein <c># TYPE base histogram</c>.</summary>
    public string Exposition(long timeMs)
    {
        var meta = Metadata(null);
        var sb = new StringBuilder(4096);
        var emittedType = new HashSet<string>(StringComparer.Ordinal);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in meta)
        {
            string promName = kv.Key;
            string type = kv.Value.Count > 0 ? kv.Value[0].Type : "gauge";
            string family = FamilyName(promName, type);
            if (emittedType.Add(family))
                sb.Append("# TYPE ").Append(family).Append(' ').Append(type).Append('\n');

            PromResult r;
            try { r = EvalInstant(promName, timeMs); }
            catch (PromQLExecException) { continue; }
            if (r.Kind != PromResultKind.Vector || r.Vector is null) continue;
            foreach (var s in r.Vector.Samples)
            {
                sb.Append(promName);
                AppendTextLabels(sb, s.Labels);
                sb.Append(' ').Append(FormatVal(s.Value)).Append(' ').Append(s.TimestampMs).Append('\n');
            }
        }
        if (sb.Length == 0) sb.Append("# Heimdall: no metrics in lookback window\n");
        return sb.ToString();
    }

    private static string FamilyName(string promName, string type)
    {
        if (type != "histogram") return promName;
        foreach (var s in new[] { "_bucket", "_sum", "_count" })
            if (promName.EndsWith(s, StringComparison.Ordinal) && promName.Length > s.Length)
                return promName.Substring(0, promName.Length - s.Length);
        return promName;
    }

    private static void AppendTextLabels(StringBuilder sb, SeriesLabels labels)
    {
        bool first = true;
        foreach (var kv in labels)
        {
            if (kv.Key == "__name__") continue;
            sb.Append(first ? '{' : ',');
            sb.Append(kv.Key).Append("=\"").Append(EscapeText(kv.Value)).Append('"');
            first = false;
        }
        if (!first) sb.Append('}');
    }

    private static string EscapeText(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string FormatVal(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "+Inf";
        if (double.IsNegativeInfinity(v)) return "-Inf";
        return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Metrik-Metadatum (Prom /metadata-Shape).</summary>
public sealed record MetricMeta(string Type, string Help, string Unit);