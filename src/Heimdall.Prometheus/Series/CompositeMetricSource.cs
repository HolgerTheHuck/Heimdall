using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// CompositeMetricSource — vereint den realen Storage-Source mit dem
// RedMetricsProvider. Routet FetchPoints nach OTel-Namen: RED-Namen
// (http_requests, http_request_duration) gehen an RED, alles andere an den
// realen Source. Discovery (Namen/Labels/Werte) ist die Vereinigung beider —
// so erscheinen RED-Serien in /api/v1/labels, /series, /label/.../values und
// Grafana entdeckt sie. Storage-agnostisch: haengt nur an IHeimdallMetricSource.
// ---------------------------------------------------------------------------

/// <summary>
/// Vereinigt den realen Storage-Source mit dem <see cref="RedMetricsProvider"/>: RED-Namen
/// (<c>http_requests</c>, <c>http_request_duration</c>) routen an RED, alle anderen an den
/// realen Source; Discovery (Namen/Labels/Werte) ist die Vereinigung. Siehe Dateikommentar.
/// </summary>
public sealed class CompositeMetricSource : IHeimdallMetricSource
{
    private readonly IHeimdallMetricSource _real;
    private readonly RedMetricsProvider _red;

    /// <summary>Erzeugt das Composite aus realem Source und RED-Provider.</summary>
    public CompositeMetricSource(IHeimdallMetricSource real, RedMetricsProvider red)
    { _real = real; _red = red; }

    /// <summary>Vereinigung der OTel-Metriknamen beider Quellen.</summary>
    public IReadOnlyList<string> ListMetricNames(long? fromUnixNano = null, long? toUnixNano = null)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in _real.ListMetricNames(fromUnixNano, toUnixNano)) set.Add(n);
        foreach (var n in _red.ListMetricNames(fromUnixNano, toUnixNano)) set.Add(n);
        return new List<string>(set);
    }

    /// <summary>Vereinigung der rohen Label-Namen beider Quellen.</summary>
    public IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? matchers = null,
        long? fromUnixNano = null, long? toUnixNano = null)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in _real.ListLabelNames(matchers, fromUnixNano, toUnixNano)) set.Add(n);
        foreach (var n in _red.ListLabelNames(matchers, fromUnixNano, toUnixNano)) set.Add(n);
        return new List<string>(set);
    }

    /// <summary>Vereinigung der Werte eines Labels (OTel-Key) beider Quellen.</summary>
    public IReadOnlyList<string> ListLabelValues(string labelName,
        IReadOnlyList<HLabelMatcher>? matchers = null, long? fromUnixNano = null, long? toUnixNano = null)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var v in _real.ListLabelValues(labelName, matchers, fromUnixNano, toUnixNano)) set.Add(v);
        foreach (var v in _red.ListLabelValues(labelName, matchers, fromUnixNano, toUnixNano)) set.Add(v);
        return new List<string>(set);
    }

    /// <summary>Holt Punkte: RED-Namen an RED, alle anderen an real, vereinigt.</summary>
    public IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery query)
    {
        var redNames = new List<string>();
        var realNames = new List<string>();
        foreach (var n in query.Names)
        {
            if (n == RedMetricsProvider.RequestsName || n == RedMetricsProvider.DurationName) redNames.Add(n);
            else realNames.Add(n);
        }

        var result = new List<HMetricPointView>();
        if (realNames.Count > 0)
            result.AddRange(_real.FetchPoints(query with { Names = realNames }));
        if (redNames.Count > 0)
            result.AddRange(_red.FetchPoints(query with { Names = redNames }));
        return result;
    }
}