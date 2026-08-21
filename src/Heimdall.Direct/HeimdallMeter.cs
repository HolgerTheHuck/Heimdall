using System;
using System.Collections.Generic;
using Heimdall;

namespace Heimdall.Direct;

/// <summary>
/// Meter: erzeugt Counter/Histogram/Gauge. Jeder Add/Record/Set-Aufruf emittiert
/// (noch ohne In-Process-Aggregation) einen <see cref="HMetricPoint"/>. Fuer
/// Counter wird der kumulative Wert pro Instrument gefuehrt; Histogramm legt jeden
/// Wert in die konfigurierten Buckets. Aggregation/Downsampling ist ein spaeterer
/// Schritt.
/// </summary>
internal sealed class HeimdallMeter : IHeimdallMeter
{
    private readonly HeimdallHub _hub;
    private readonly HScope? _scope;

    public HeimdallMeter(HeimdallHub hub, HScope? scope)
    {
        _hub = hub;
        _scope = scope;
    }

    public IHeimdallCounter CreateCounter(string name, string? unit = null)
        => new HeimdallCounter(_hub, _scope, name, unit);

    public IHeimdallHistogram CreateHistogram(string name, string? unit = null)
        => new HeimdallHistogram(_hub, _scope, name, unit, DefaultHistogramBounds);

    public IHeimdallGauge CreateGauge(string name, string? unit = null)
        => new HeimdallGauge(_hub, _scope, name, unit);

    /// <summary>Default-Explizit-Bounds (OTel-Histogramm-Stil, fuer Latenzen in ms).</summary>
    internal static readonly double[] DefaultHistogramBounds =
        new[] { 0d, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000 };

    internal static long NowNs() => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;
    internal static readonly long UnixEpochTicks =
        new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
}

internal sealed class HeimdallCounter : IHeimdallCounter
{
    private readonly HeimdallHub _hub;
    private readonly HScope? _scope;
    private readonly string _name;
    private readonly string? _unit;
    private double _value;

    public HeimdallCounter(HeimdallHub hub, HScope? scope, string name, string? unit)
    {
        _hub = hub; _scope = scope; _name = name; _unit = unit;
    }

    public void Add(double value, params HAttribute[] attributes)
    {
        if (HeimdallRecording.IsSuppressed) return;
        _value += value;
        _hub.WriteMetric(new HMetricPoint(_name, _unit, HMetricType.Sum, HTemporality.Cumulative,
            HeimdallMeter.NowNs(), _value, null, null, null, null, null, null,
            attributes is null || attributes.Length == 0 ? HAttributes.Empty : attributes,
            _hub.Resource, _scope));
    }
}

internal sealed class HeimdallHistogram : IHeimdallHistogram
{
    private readonly HeimdallHub _hub;
    private readonly HScope? _scope;
    private readonly string _name;
    private readonly string? _unit;
    private readonly double[] _bounds;

    public HeimdallHistogram(HeimdallHub hub, HScope? scope, string name, string? unit, double[] bounds)
    {
        _hub = hub; _scope = scope; _name = name; _unit = unit; _bounds = bounds;
    }

    public void Record(double value, params HAttribute[] attributes)
    {
        if (HeimdallRecording.IsSuppressed) return;
        var buckets = new long[_bounds.Length + 1];
        int idx = 0;
        while (idx < _bounds.Length && value > _bounds[idx]) idx++;
        // idx = Anzahl der Bounds, die < value sind => Bucket index = idx (0..bounds.Length)
        for (int i = idx; i < buckets.Length; i++) buckets[i] = 1; // kumulativ
        _hub.WriteMetric(new HMetricPoint(_name, _unit, HMetricType.Histogram, HTemporality.Cumulative,
            HeimdallMeter.NowNs(), 0d, 1L, value, value, value,
            buckets, _bounds,
            attributes is null || attributes.Length == 0 ? HAttributes.Empty : attributes,
            _hub.Resource, _scope));
    }
}

internal sealed class HeimdallGauge : IHeimdallGauge
{
    private readonly HeimdallHub _hub;
    private readonly HScope? _scope;
    private readonly string _name;
    private readonly string? _unit;

    public HeimdallGauge(HeimdallHub hub, HScope? scope, string name, string? unit)
    {
        _hub = hub; _scope = scope; _name = name; _unit = unit;
    }

    public void Set(double value, params HAttribute[] attributes)
    {
        if (HeimdallRecording.IsSuppressed) return;
        _hub.WriteMetric(new HMetricPoint(_name, _unit, HMetricType.Gauge, HTemporality.Unspecified,
            HeimdallMeter.NowNs(), value, null, null, null, null, null, null,
            attributes is null || attributes.Length == 0 ? HAttributes.Empty : attributes,
            _hub.Resource, _scope));
    }
}