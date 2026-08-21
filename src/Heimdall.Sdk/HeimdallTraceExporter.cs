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

    public HeimdallTraceExporter(IHeimdallSink sink, HResource resource)
    {
        _sink = sink;
        _resource = resource;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        var list = new List<HSpan>();
        foreach (var activity in batch)
        {
            try { list.Add(ToSpan(activity)); }
            catch { /* eine fehlerhafte Activity verwirft nur sich selbst */ }
        }
        if (list.Count == 0) return ExportResult.Success;
        try { _sink.WriteSpans(list); }
        catch { /* Storage-Fehler darf den SDK-Pipeline nicht infizieren */ }
        return ExportResult.Success;
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