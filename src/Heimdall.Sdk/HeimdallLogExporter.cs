using System;
using System.Collections.Generic;
using Heimdall;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Heimdall.Sdk;

/// <summary>
/// In-Process-Exporter fuer Logs: wandelt SDK-<see cref="LogRecord"/>-Batches in
/// <see cref="HLogRecord"/> um und schreibt sie direkt in den Heimdall-Sink.
/// </summary>
internal sealed class HeimdallLogExporter : BaseExporter<LogRecord>
{
    private readonly IHeimdallSink _sink;
    private readonly HResource _resource;
    private readonly IReadOnlyList<string>? _excludeCategories;

    public HeimdallLogExporter(IHeimdallSink sink, HResource resource, IReadOnlyList<string>? excludeCategories = null)
    {
        _sink = sink;
        _resource = resource;
        _excludeCategories = excludeCategories;
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        var list = new List<HLogRecord>();
        foreach (var r in batch)
        {
            try
            {
                // Heimdall-eigene Diagnose-Logs (Kategorie-Prefix, z. B. "Heimdall.")
                // nicht ins Dashboard schreiben.
                if (_excludeCategories is not null
                    && SdkConvert.StartsWithAny(r.CategoryName, _excludeCategories)) continue;
                list.Add(ToLog(r));
            }
            catch { }
        }
        if (list.Count == 0) return ExportResult.Success;
        try { _sink.WriteLogs(list); } catch { }
        return ExportResult.Success;
    }

    private HLogRecord ToLog(LogRecord r)
    {
        var sev = MapSeverity(r.LogLevel);
        var body = r.FormattedMessage ?? r.Body;
        return new HLogRecord(
            SdkConvert.ToUnixNano(r.Timestamp),
            sev,
            sev.ToString().ToUpperInvariant(),
            body,
            SdkConvert.TraceIdBytes(r.TraceId),
            SdkConvert.SpanIdBytes(r.SpanId),
            SdkConvert.MapTags(r.Attributes),
            _resource,
            r.CategoryName is null ? null : new HScope(r.CategoryName, null, HAttributes.Empty));
    }

    private static HSeverity MapSeverity(LogLevel level) => level switch
    {
        LogLevel.Trace => HSeverity.Trace,
        LogLevel.Debug => HSeverity.Debug,
        LogLevel.Information => HSeverity.Info,
        LogLevel.Warning => HSeverity.Warn,
        LogLevel.Error => HSeverity.Error,
        LogLevel.Critical => HSeverity.Fatal,
        _ => HSeverity.Info,
    };

    protected override bool OnShutdown(int timeoutMilliseconds) => true;
}