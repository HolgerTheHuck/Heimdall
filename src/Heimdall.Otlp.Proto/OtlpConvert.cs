using System;
using System.Collections.Generic;
using Google.Protobuf;
using Heimdall;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using LogRecord = OpenTelemetry.Proto.Logs.V1.LogRecord;
using ResourceLogs = OpenTelemetry.Proto.Logs.V1.ResourceLogs;
using ScopeLogs = OpenTelemetry.Proto.Logs.V1.ScopeLogs;
using ResourceMetrics = OpenTelemetry.Proto.Metrics.V1.ResourceMetrics;
using ScopeMetrics = OpenTelemetry.Proto.Metrics.V1.ScopeMetrics;
using ResourceSpans = OpenTelemetry.Proto.Trace.V1.ResourceSpans;
using ScopeSpans = OpenTelemetry.Proto.Trace.V1.ScopeSpans;
// Im vendierten opentelemetry-proto v1.7.0 ist SpanKind (anders als im ehemaligen
// Standalone-Paket) innerhalb von Span geschachtelt → Alias hält den Code sauber.
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace Heimdall.Otlp;

/// <summary>
/// Konvertiert OTLP-Collector-Requests (Trace/Logs/Metrics) in das kanonische
/// Heimdall-Modell (<see cref="HSpan"/>/<see cref="HLogRecord"/>/<see cref="HMetricPoint"/>)
/// — die Gegenrichtung zum Sdk-Exporter (<c>Heimdall.Sdk/OtlpConvert</c>-Spiegel von
/// <c>SdkConvert</c>). IDs sind im OTLP bereits rohe <see cref="ByteString"/> (kein
/// Hex-Roundtrip). Werferfrei pro Element: ein fehlerhaftes Span/Log/Metric verwirft
/// nur sich selbst, nicht den Batch. Intern (via IVT fuer Tests sichtbar).
/// </summary>
internal static class OtlpConvert
{
    // ---------------------------------------------------------------------
    // Traces
    // ---------------------------------------------------------------------

    public static IReadOnlyList<HSpan> ToSpans(OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest req)
    {
        var result = new List<HSpan>();
        if (req is null) return result;
        foreach (var rs in req.ResourceSpans)
        {
            if (rs is null) continue;
            var resource = ToResource(rs.Resource);
            foreach (var ss in rs.ScopeSpans)
            {
                if (ss is null) continue;
                var scope = ToScope(ss.Scope);
                foreach (var span in ss.Spans)
                {
                    if (span is null) continue;
                    var hs = ToSpan(span, resource, scope);
                    if (hs is not null) result.Add(hs);
                }
            }
        }
        return result;
    }

    private static HSpan? ToSpan(Span s, HResource? resource, HScope? scope)
    {
        try
        {
            var traceId = Bytes(s.TraceId);
            var spanId = Bytes(s.SpanId);
            if (traceId is null) return null;   // ohne TraceId nicht abfragbar
            byte[]? parent = s.ParentSpanId.IsEmpty ? null : Bytes(s.ParentSpanId);

            return new HSpan(
                traceId,
                spanId ?? Array.Empty<byte>(),
                parent,
                s.Name ?? string.Empty,
                MapKind(s.Kind),
                ToLong(s.StartTimeUnixNano),
                ToLong(s.EndTimeUnixNano),
                MapStatus(s.Status),
                s.Status?.Message,
                ToAttrs(s.Attributes),
                ToEvents(s.Events),
                ToLinks(s.Links),
                resource,
                scope);
        }
        catch { return null; }
    }

    private static IReadOnlyList<HSpanEvent> ToEvents(IList<Span.Types.Event> events)
    {
        if (events is null || events.Count == 0) return Array.Empty<HSpanEvent>();
        var list = new List<HSpanEvent>(events.Count);
        foreach (var e in events)
        {
            if (e is null) continue;
            list.Add(new HSpanEvent(ToLong(e.TimeUnixNano), e.Name ?? string.Empty, ToAttrs(e.Attributes)));
        }
        return list;
    }

    private static IReadOnlyList<HSpanLink> ToLinks(IList<Span.Types.Link> links)
    {
        if (links is null || links.Count == 0) return Array.Empty<HSpanLink>();
        var list = new List<HSpanLink>(links.Count);
        foreach (var l in links)
        {
            if (l is null) continue;
            var tid = Bytes(l.TraceId);
            var sid = Bytes(l.SpanId);
            if (tid is null || sid is null) continue;
            list.Add(new HSpanLink(tid, sid, ToAttrs(l.Attributes)));
        }
        return list;
    }

    private static HSpanKind MapKind(SpanKind k) => k switch
    {
        SpanKind.Server => HSpanKind.Server,
        SpanKind.Client => HSpanKind.Client,
        SpanKind.Producer => HSpanKind.Producer,
        SpanKind.Consumer => HSpanKind.Consumer,
        _ => HSpanKind.Internal,     // Unspecified → Internal
    };

    private static HStatusCode MapStatus(Status? st)
    {
        if (st is null) return HStatusCode.Unset;
        return st.Code switch
        {
            Status.Types.StatusCode.Ok => HStatusCode.Ok,
            Status.Types.StatusCode.Error => HStatusCode.Error,
            _ => HStatusCode.Unset,
        };
    }

    // ---------------------------------------------------------------------
    // Logs
    // ---------------------------------------------------------------------

    public static IReadOnlyList<HLogRecord> ToLogs(OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest req)
    {
        var result = new List<HLogRecord>();
        if (req is null) return result;
        foreach (var rl in req.ResourceLogs)
        {
            if (rl is null) continue;
            var resource = ToResource(rl.Resource);
            foreach (var sl in rl.ScopeLogs)
            {
                if (sl is null) continue;
                var scope = ToScope(sl.Scope);
                foreach (var lr in sl.LogRecords)
                {
                    if (lr is null) continue;
                    var rec = ToLog(lr, resource, scope);
                    if (rec is not null) result.Add(rec);
                }
            }
        }
        return result;
    }

    private static HLogRecord? ToLog(LogRecord r, HResource? resource, HScope? scope)
    {
        try
        {
            return new HLogRecord(
                ToLong(r.TimeUnixNano),
                MapSeverity(r.SeverityNumber),
                string.IsNullOrEmpty(r.SeverityText) ? null : r.SeverityText,
                AnyToString(r.Body),
                Bytes(r.TraceId),
                Bytes(r.SpanId),
                ToAttrs(r.Attributes),
                resource,
                scope);
        }
        catch { return null; }
    }

    private static HSeverity MapSeverity(SeverityNumber sn)
    {
        // OTel SeverityNumber 1..24 in Bändern (Trace/Debug/Info/Warn/Error/Fatal),
        // 0 = Unspecified → Info. Heimdall-Bänder: Trace=1, Debug=5, Info=9, Warn=13, Error=17, Fatal=21.
        int n = (int)sn;
        if (n <= 0) return HSeverity.Info;
        if (n <= 4) return HSeverity.Trace;
        if (n <= 8) return HSeverity.Debug;
        if (n <= 12) return HSeverity.Info;
        if (n <= 16) return HSeverity.Warn;
        if (n <= 20) return HSeverity.Error;
        return HSeverity.Fatal;
    }

    // ---------------------------------------------------------------------
    // Metrics
    // ---------------------------------------------------------------------

    public static IReadOnlyList<HMetricPoint> ToMetrics(OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceRequest req)
    {
        var result = new List<HMetricPoint>();
        if (req is null) return result;
        foreach (var rm in req.ResourceMetrics)
        {
            if (rm is null) continue;
            var resource = ToResource(rm.Resource);
            foreach (var sm in rm.ScopeMetrics)
            {
                if (sm is null) continue;
                var scope = ToScope(sm.Scope);
                foreach (var m in sm.Metrics)
                {
                    if (m is null) continue;
                    ConvertMetric(m, resource, scope, result);
                }
            }
        }
        return result;
    }

    private static void ConvertMetric(Metric m, HResource? resource, HScope? scope, List<HMetricPoint> result)
    {
        var name = m.Name;
        var unit = string.IsNullOrEmpty(m.Unit) ? null : m.Unit;

        // Oneof über Null-Check der Nachrichten-Felder (umgeht enum-Namens-Fragen).
        if (m.Gauge is { } gauge)
        {
            foreach (var dp in gauge.DataPoints)
            {
                if (dp is null) continue;
                result.Add(new HMetricPoint(name, unit, HMetricType.Gauge, HTemporality.Unspecified,
                    ToLong(dp.TimeUnixNano), NumberValue(dp), null, null, null, null, null, null,
                    ToAttrs(dp.Attributes), resource, scope));
            }
        }
        else if (m.Sum is { } sum)
        {
            var temp = MapTemporality(sum.AggregationTemporality);
            foreach (var dp in sum.DataPoints)
            {
                if (dp is null) continue;
                result.Add(new HMetricPoint(name, unit, HMetricType.Sum, temp,
                    ToLong(dp.TimeUnixNano), NumberValue(dp), null, null, null, null, null, null,
                    ToAttrs(dp.Attributes), resource, scope));
            }
        }
        else if (m.Histogram is { } hist)
        {
            var temp = MapTemporality(hist.AggregationTemporality);
            foreach (var dp in hist.DataPoints)
            {
                if (dp is null) continue;
                var counts = new List<long>();
                foreach (var c in dp.BucketCounts) counts.Add((long)c);
                var bounds = new List<double>();
                foreach (var b in dp.ExplicitBounds) bounds.Add(b);
                double? min = dp.HasMin ? dp.Min : null;
                double? max = dp.HasMax ? dp.Max : null;
                double sumVal = dp.HasSum ? dp.Sum : 0d;
                result.Add(new HMetricPoint(name, unit, HMetricType.Histogram, temp,
                    ToLong(dp.TimeUnixNano), sumVal, (long)dp.Count, sumVal, min, max,
                    counts.Count == 0 ? null : counts, bounds.Count == 0 ? null : bounds,
                    ToAttrs(dp.Attributes), resource, scope));
            }
        }
        // ExponentialHistogram / Summary → bewusst nicht (DESIGN §2: Counter/Sum/Gauge/Histogram).
    }

    private static double NumberValue(NumberDataPoint dp)
    {
        // Skalarer Oneof: AsInt (long) oder AsDouble (double). ValueCase entscheidet.
        if (dp.ValueCase == NumberDataPoint.ValueOneofCase.AsInt)
            return (double)dp.AsInt;
        return dp.AsDouble;
    }

    private static HTemporality MapTemporality(AggregationTemporality t) => t switch
    {
        AggregationTemporality.Delta => HTemporality.Delta,
        AggregationTemporality.Cumulative => HTemporality.Cumulative,
        _ => HTemporality.Unspecified,
    };

    // ---------------------------------------------------------------------
    // Gemeinsame Helfer
    // ---------------------------------------------------------------------

    private static HResource? ToResource(Resource? r)
    {
        if (r is null) return null;
        var attrs = ToAttrs(r.Attributes);
        return attrs.Count == 0 ? null : new HResource(attrs);
    }

    private static HScope? ToScope(InstrumentationScope? s)
    {
        if (s is null || string.IsNullOrEmpty(s.Name)) return null;
        return new HScope(s.Name, string.IsNullOrEmpty(s.Version) ? null : s.Version, ToAttrs(s.Attributes));
    }

    private static IReadOnlyList<HAttribute> ToAttrs(IList<KeyValue> attrs)
    {
        if (attrs is null || attrs.Count == 0) return HAttributes.Empty;
        var list = new List<HAttribute>(attrs.Count);
        foreach (var kv in attrs)
        {
            if (kv is null || string.IsNullOrEmpty(kv.Key)) continue;
            var v = AnyToObject(kv.Value);
            if (v is null) continue;
            list.Add(new HAttribute(kv.Key, v));
        }
        return list.Count == 0 ? HAttributes.Empty : list;
    }

    private static object? AnyToObject(AnyValue? v)
    {
        if (v is null) return null;
        switch (v.ValueCase)
        {
            case AnyValue.ValueOneofCase.StringValue: return v.StringValue;
            case AnyValue.ValueOneofCase.BoolValue: return v.BoolValue;
            case AnyValue.ValueOneofCase.IntValue: return (long)v.IntValue;
            case AnyValue.ValueOneofCase.DoubleValue: return v.DoubleValue;
            case AnyValue.ValueOneofCase.BytesValue: return v.BytesValue.ToByteArray();
            // Komplex (Array/KeyValueList) → kanonischer JSON-String, damit der Wert
            // erhalten bleibt und HeimdallJson.WriteValue ihn als String persistiert.
            case AnyValue.ValueOneofCase.ArrayValue:
            case AnyValue.ValueOneofCase.KvlistValue:
                return JsonFormatter.Default.Format(v);
            default: return null;
        }
    }

    /// <summary>Body/AnyValue als lesbaren String (für HLogRecord.Body).</summary>
    private static string? AnyToString(AnyValue? v)
    {
        if (v is null) return null;
        return v.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => v.StringValue,
            AnyValue.ValueOneofCase.BoolValue => v.BoolValue ? "true" : "false",
            AnyValue.ValueOneofCase.IntValue => v.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AnyValue.ValueOneofCase.DoubleValue => v.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            AnyValue.ValueOneofCase.BytesValue => Convert.ToHexString(v.BytesValue.ToByteArray()).ToLowerInvariant(),
            AnyValue.ValueOneofCase.ArrayValue => JsonFormatter.Default.Format(v),
            AnyValue.ValueOneofCase.KvlistValue => JsonFormatter.Default.Format(v),
            _ => null,
        };
    }

    /// <summary>ByteString → byte[]; leer → null (Kennzahl für „nicht gesetzt").</summary>
    private static byte[]? Bytes(ByteString bs)
    {
        if (bs is null || bs.IsEmpty) return null;
        return bs.ToByteArray();
    }

    private static long ToLong(ulong v) => (long)v;
}