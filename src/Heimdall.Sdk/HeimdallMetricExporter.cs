using System;
using System.Collections.Generic;
using Heimdall;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Heimdall.Sdk;

/// <summary>
/// In-Process-Exporter fuer Metriken: iteriert die <see cref="MetricPoint"/>s
/// jedes SDK-<see cref="Metric"/> und emittiert pro Punkt einen
/// <see cref="HMetricPoint"/> (Gauge/Sum kumulativ, Histogramm mit Buckets).
/// </summary>
internal sealed class HeimdallMetricExporter : BaseExporter<Metric>
{
    private readonly IHeimdallSink _sink;
    private readonly HResource _resource;
    private readonly IReadOnlyList<string>? _excludeRoutes;

    public HeimdallMetricExporter(IHeimdallSink sink, HResource resource, IReadOnlyList<string>? excludeRoutes = null)
    {
        _sink = sink;
        _resource = resource;
        _excludeRoutes = excludeRoutes;
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        var list = new List<HMetricPoint>();
        foreach (var metric in batch)
        {
            try { Collect(metric, list); } catch { }
        }
        if (list.Count == 0) return ExportResult.Success;
        try { _sink.WriteMetrics(list); } catch { }
        return ExportResult.Success;
    }

    private void Collect(Metric metric, List<HMetricPoint> list)
    {
        var name = metric.Name;
        var unit = string.IsNullOrEmpty(metric.Unit) ? null : metric.Unit;
        var mt = metric.MetricType;

        foreach (ref readonly var mp in metric.GetMetricPoints())
        {
            // MetricTags ist intern; nur ueber var + pattern-basiertes foreach erreichbar.
            var attrsList = new List<HAttribute>();
            string? route = null;
            foreach (var kv in mp.Tags)
            {
                if (!string.IsNullOrEmpty(kv.Key)) attrsList.Add(new HAttribute(kv.Key, kv.Value));
                if (kv.Key == "http.route" && kv.Value is string s) route = s;
            }

            // Heimdall-eigene Dashboard-Routes pro Punkt verwerfen (aspnetcore-Meter
            // taggen http.server.request.duration etc. mit http.route-Template).
            if (_excludeRoutes is not null && SdkConvert.StartsWithAny(route, _excludeRoutes)) continue;
            IReadOnlyList<HAttribute> attrs = attrsList.Count == 0 ? HAttributes.Empty : attrsList;
            var ts = SdkConvert.ToUnixNano(mp.EndTime);
            HMetricType htype;
            HTemporality htemp;
            double value;
            long? count = null;
            double? sum = null, min = null, max = null;
            IReadOnlyList<long>? bucketCounts = null;
            IReadOnlyList<double>? explicitBounds = null;

            switch (mt)
            {
                case MetricType.LongGauge:
                    htype = HMetricType.Gauge; htemp = HTemporality.Unspecified; value = mp.GetGaugeLastValueLong();
                    break;
                case MetricType.DoubleGauge:
                    htype = HMetricType.Gauge; htemp = HTemporality.Unspecified; value = mp.GetGaugeLastValueDouble();
                    break;
                case MetricType.LongSum:
                case MetricType.LongSumNonMonotonic:
                    htype = HMetricType.Sum; htemp = HTemporality.Cumulative; value = mp.GetSumLong();
                    break;
                case MetricType.DoubleSum:
                case MetricType.DoubleSumNonMonotonic:
                    htype = HMetricType.Sum; htemp = HTemporality.Cumulative; value = mp.GetSumDouble();
                    break;
                case MetricType.Histogram:
                case MetricType.ExponentialHistogram:
                    htype = HMetricType.Histogram; htemp = HTemporality.Cumulative;
                    sum = mp.GetHistogramSum();
                    count = mp.GetHistogramCount();
                    if (mp.TryGetHistogramMinMaxValues(out double hn, out double hx)) { min = hn; max = hx; }
                    value = sum ?? 0d;
                    var buckets = mp.GetHistogramBuckets();
                    var bc = new List<long>();
                    var eb = new List<double>();
                    foreach (var bucket in buckets) { bc.Add(bucket.BucketCount); eb.Add(bucket.ExplicitBound); }
                    if (bc.Count > 0) { bucketCounts = bc; explicitBounds = eb; }
                    break;
                default:
                    continue;
            }

            list.Add(new HMetricPoint(
                name, unit, htype, htemp, ts, value, count, sum, min, max,
                bucketCounts, explicitBounds, attrs, _resource, null));
        }
    }

    protected override bool OnShutdown(int timeoutMilliseconds) => true;
}