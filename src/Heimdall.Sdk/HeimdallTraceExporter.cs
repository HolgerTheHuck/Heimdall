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

    /// <summary>true, wenn der Span einen http.route-/http.target-/url.path-Tag
    /// trägt, der mit einem der Prefixe beginnt (Heimdall-eigene Routes).</summary>
    private static bool ExcludeRoute(Activity a, IReadOnlyList<string> prefixes)
    {
        foreach (var kv in a.Tags)
        {
            if (kv.Value is null) continue;
            if ((kv.Key == "http.route" || kv.Key == "http.target" || kv.Key == "url.path")
                && SdkConvert.StartsWithAny(kv.Value, prefixes)) return true;
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