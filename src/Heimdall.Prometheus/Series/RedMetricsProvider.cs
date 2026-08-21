using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// RedMetricsProvider — leitet klassische RED-Metriken (Rate/Errors/Duration)
// aus Server-Spans ab, sodass Community-Web-Dashboards auch ohne eigene Meter-
// Instrumentierung laufen. Eingabe: IHeimdallQuery.ListSpans(Kind=Server).
//
// Erzeugte OTel-Metriken (Delta-Temporalitaet — der SeriesResolver kumuliert
// sie je Serie zu monotonen Prom-Countern/Histogrammen):
//   http_requests        (Counter, unit "1")  -> http_requests_total
//   http_request_duration(Histogram, unit "s")-> http_request_duration_seconds_{bucket,sum,count}
// Gruppierung nach (job, http.route, http.method, http.response.status_code);
// http.method/Status aus Span-Attrs (Fallback GET bzw. 200/500 aus StatusCode).
// Buckets: [0.005,0.01,0.025,0.05,0.1,0.25,0.5,1,2.5,5,10,+Inf]. Zeit-Bucket
// grob dynamisch (max ~600 Punkte/Serie), um lange Fenster bezahlbar zu halten.
// ---------------------------------------------------------------------------

/// <summary>
/// Leitet <c>http_requests_total</c> und <c>http_request_duration_seconds_*</c>
/// aus Server-Spans ab (siehe Dateikommentar). Implementiert
/// <see cref="IHeimdallMetricSource"/> und wird ueber <see cref="CompositeMetricSource"/>
/// mit dem realen Source vereinigt.
/// </summary>
public sealed class RedMetricsProvider : IHeimdallMetricSource
{
    /// <summary>OTel-Name des abgeleiteten Request-Counters (→ <c>http_requests_total</c>).</summary>
    public const string RequestsName = "http_requests";
    /// <summary>OTel-Name des abgeleiteten Latenz-Histogramms (→ <c>http_request_duration_seconds_*</c>).</summary>
    public const string DurationName = "http_request_duration";

    private static readonly double[] _bounds = { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 };
    private const int MaxBuckets = 600;
    private const int SpanLimit = 100_000;

    private readonly IHeimdallQuery _query;

    /// <summary>Erzeugt den Provider ueber den Span-Lese-Vertrag <paramref name="query"/>.</summary>
    public RedMetricsProvider(IHeimdallQuery query) { _query = query; }

    /// <summary>Die beiden abgeleiteten OTel-Metriknamen.</summary>
    public IReadOnlyList<string> ListMetricNames(long? fromUnixNano = null, long? toUnixNano = null)
        => new[] { RequestsName, DurationName };

    /// <summary>Raw-Label-Namen der RED-Serien (service.name, http.route, http.method, http.response.status_code).</summary>
    public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? matchers = null,
        long? fromUnixNano = null, long? toUnixNano = null)
        => new[] { "service.name", "http.route", "http.method", "http.response.status_code" };

    /// <summary>Distincte Werte eines RED-Labels (OTel-Key) im Fenster — mit denselben
    /// Fallbacks wie <see cref="FetchPoints"/> (Method→GET, Status aus StatusCode, job→unknown).</summary>
    public IReadOnlyList<string> ListLabelValues(string labelName,
        IReadOnlyList<HLabelMatcher>? matchers = null, long? fromUnixNano = null, long? toUnixNano = null)
    {
        var spans = FetchSpans(fromUnixNano, toUnixNano);
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in spans)
        {
            var attrs = ParseAttrs(s.AttrsJson);
            var res = ParseAttrs(s.ResourceJson);
            string? val = labelName switch
            {
                "service.name" => TryGetAttr(res, "service.name", out var job) ? job : "unknown",
                "http.method" => TryGetAttr(attrs, "http.method", out var m) ? m : "GET",
                "http.response.status_code" => TryGetAttr(attrs, "http.response.status_code", out var st)
                    ? st : ResolveStatus(null, s.StatusCode),
                _ => TryGetAttr(attrs, labelName, out var v) ? v : null,
            };
            if (!string.IsNullOrEmpty(val)) values.Add(val);
        }
        return new List<string>(values).OrderBy(v => v, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Expandiert die Server-Spans zu RED-Metrikpunkten (Delta) im Fenster.</summary>
    public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery q)
    {
        bool wantReq = q.Names.Contains(RequestsName);
        bool wantDur = q.Names.Contains(DurationName);
        if (!wantReq && !wantDur) return Array.Empty<HMetricPointView>();

        long from = q.FromUnixNano ?? 0;
        long to = q.ToUnixNano ?? long.MaxValue;
        var spans = FetchSpans(from, to);
        if (spans.Count == 0) return Array.Empty<HMetricPointView>();

        long windowSec = Math.Max(1, (to - from) / 1_000_000_000L);
        long bucketSec = Math.Max(1, windowSec / MaxBuckets);
        long bucketNs = bucketSec * 1_000_000_000L;

        // Gruppe (job,route,method,status) x Bucket -> Aggregat.
        var groups = new Dictionary<GroupKey, BucketAgg>();
        foreach (var s in spans)
        {
            if (s.Kind != (int)HSpanKind.Server) continue;
            long bucketStart = s.StartUnixNano / bucketNs * bucketNs;
            var attrs = ParseAttrs(s.AttrsJson);
            var res = ParseAttrs(s.ResourceJson);
            TryGetAttr(res, "service.name", out var job);
            TryGetAttr(attrs, "http.route", out var route);
            TryGetAttr(attrs, "http.method", out var method);
            TryGetAttr(attrs, "http.response.status_code", out var statusStr);
            string methodVal = string.IsNullOrEmpty(method) ? "GET" : method;
            string statusVal = ResolveStatus(statusStr, s.StatusCode);
            var key = new GroupKey(job ?? "unknown", route ?? string.Empty, methodVal, statusVal, bucketStart);
            if (!groups.TryGetValue(key, out var agg)) { agg = new BucketAgg(); groups[key] = agg; }
            agg.Count++;
            agg.Sum += s.DurationNs / 1_000_000_000.0;
            double d = s.DurationNs / 1_000_000_000.0;
            int bi = BucketIndex(d);
            agg.BucketCounts[bi]++;
        }

        var points = new List<HMetricPointView>(groups.Count * 2);
        foreach (var kv in groups)
        {
            var g = kv.Key;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service.name"] = g.Job,
                ["http.route"] = g.Route,
                ["http.method"] = g.Method,
                ["http.response.status_code"] = g.Status,
            };
            long ts = g.BucketStart;
            if (wantReq)
                points.Add(new HMetricPointView(RequestsName, "1", HMetricType.Sum, HTemporality.Delta,
                    ts, kv.Value.Count, null, null, null, null, null, null, labels, "heimdall-red"));
            if (wantDur)
                points.Add(new HMetricPointView(DurationName, "s", HMetricType.Histogram, HTemporality.Delta,
                    ts, kv.Value.Sum, kv.Value.Count, kv.Value.Sum, 0, 0,
                    kv.Value.BucketCounts, _bounds, labels, "heimdall-red"));
        }
        return points;
    }

    // --- Helfer -------------------------------------------------------------
    private IReadOnlyList<SpanRow> FetchSpans(long? from, long? to)
        => _query.ListSpans(new SpanFilter(from, to, (int)HSpanKind.Server, null, SpanLimit));

    private static int BucketIndex(double seconds)
    {
        for (int i = 0; i < _bounds.Length; i++) if (seconds <= _bounds[i]) return i;
        return _bounds.Length; // +Inf-Bucket
    }

    private static string ResolveStatus(string? statusStr, int statusCode)
    {
        if (!string.IsNullOrEmpty(statusStr)) return statusStr;
        return statusCode >= (int)HStatusCode.Error ? "500" : "200";
    }

    private static Dictionary<string, string> ParseAttrs(string? json)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = JsonValueToString(p.Value);
        }
        catch { }
        return dict;
    }

    private static bool TryGetAttr(Dictionary<string, string> attrs, string key, out string value)
        => attrs.TryGetValue(key, out value!);

    private static string JsonValueToString(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString() ?? string.Empty;
            case JsonValueKind.Number: return el.GetRawText();
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            default: return el.GetRawText();
        }
    }

    private sealed record GroupKey(string Job, string Route, string Method, string Status, long BucketStart);

    private sealed class BucketAgg
    {
        public long Count;
        public double Sum;
        public readonly long[] BucketCounts = new long[_bounds.Length + 1];
    }
}