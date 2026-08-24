using System;
using System.Collections.Generic;
using System.Diagnostics;
using Heimdall;
using OpenTelemetry;

namespace Heimdall.Sdk;

/// <summary>
/// In-Process-Exporter fuer Spans: wandelt SDK-<see cref="Activity"/>-Batches in
/// <see cref="HSpan"/> um und schreibt sie direkt in den Heimdall-<see cref="IHeimdallSink"/>.
/// Kein Netzwerk, kein OTLP. Wirft nie (Telemetrie darf den Host nicht killen).
/// </summary>
internal sealed class HeimdallTraceExporter : BaseExporter<Activity>
{
    private readonly IHeimdallSink _sink;
    private readonly HResource _resource;
    private readonly IReadOnlyList<string>? _excludeRoutes;

    public HeimdallTraceExporter(IHeimdallSink sink, HResource resource, IReadOnlyList<string>? excludeRoutes = null)
    {
        _sink = sink;
        _resource = resource;
        _excludeRoutes = excludeRoutes;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        var list = new List<HSpan>();
        foreach (var activity in batch)
        {
            try
            {
                // Heimdall-eigene Dashboard-Routes nicht als App-Verkehr erfassen.
                if (_excludeRoutes is not null && ExcludeRoute(activity, _excludeRoutes)) continue;
                list.Add(ToSpan(activity));
            }
            catch { /* eine fehlerhafte Activity verwirft nur sich selbst */ }
        }
        if (list.Count == 0) return ExportResult.Success;
        try { _sink.WriteSpans(list); }
        catch { /* Storage-Fehler darf den SDK-Pipeline nicht infizieren */ }
        return ExportResult.Success;
    }

    /// <summary>true, wenn der Span einen Route-Tag trägt, der mit einem der Prefixe
    /// beginnt (Heimdall-eigene Dashboard-Routes). Geprüft werden
    /// <c>http.route</c>/<c>http.target</c>/<c>url.path</c> (OTel-Semantic-Conventions)
    /// UND <c>aspnetmvc.route</c> (Heimdalls eigene Enrichment-Middleware, gesetzt
    /// für jeden gematchten Endpunkt — verlässlicher Fallback, falls die
    /// ASP.NET-Instrumentation <c>http.route</c> nicht oder erst spät setzt).
    /// Wichtig: iteriert wird <see cref="Activity.TagObjects"/> (alle Tags), NICHT
    /// <see cref="Activity.Tags"/> — letzteres liefert nur via string-Überladung
    /// gesetzte Tags; die OTel-Instrumentation setzt <c>http.route</c> aber via
    /// object-Überladung, sodass er nur in <c>TagObjects</c> (wie <c>ToSpan</c>)
    /// sichtbar ist. Würde der Filter <c>Tags</c> lesen, sähe er <c>http.route</c>
    /// nicht → der /otel-Span würde gespeichert statt verworfen.</summary>
    private static bool ExcludeRoute(Activity a, IReadOnlyList<string> prefixes)
    {
        foreach (var kv in a.TagObjects)
        {
            if (kv.Value is not string s) continue;
            if ((kv.Key == "http.route" || kv.Key == "http.target" || kv.Key == "url.path"
                 || kv.Key == "aspnetmvc.route")
                && SdkConvert.StartsWithAny(s, prefixes)) return true;
        }
        return false;
    }

    private HSpan ToSpan(Activity a)
    {
        var traceId = SdkConvert.TraceIdBytes(a.Context.TraceId);
        var spanId = SdkConvert.SpanIdBytes(a.Context.SpanId);
        byte[]? parent = a.ParentSpanId != default
            ? SdkConvert.SpanIdBytes(a.ParentSpanId)
            : null;
        var startNs = SdkConvert.ToUnixNano(a.StartTimeUtc);
        var endNs = SdkConvert.ToUnixNano(a.StartTimeUtc.Add(a.Duration));

        return new HSpan(
            traceId ?? Array.Empty<byte>(),
            spanId ?? Array.Empty<byte>(),
            parent,
            a.DisplayName ?? string.Empty,
            SdkConvert.MapKind(a.Kind),
            startNs, endNs,
            SdkConvert.MapStatus(a.Status),
            a.StatusDescription,
            SdkConvert.MapTags(a.TagObjects),
            SdkConvert.MapEvents(a.Events),
            SdkConvert.MapLinks(a.Links),
            _resource,
            null);
    }

    protected override bool OnShutdown(int timeoutMilliseconds) => true;
}