using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using Serilog.Events;

namespace Serilog.Sinks.Heimdall;

/// <summary>
/// Mappt Serilog-<see cref="LogEvent"/>-Batches in OTLP-<c>ExportLogsServiceRequest</c>s
/// (in-memory, Protobuf). Der Sink ist damit eine dünne Schicht: die Konvertierung
/// OTLP→Heimdall übernimmt die existierende <c>OtlpConvert</c>-Konvertierung — es gibt
/// genau EINE neue Abbildungsschicht (Serilog→OTLP), keine eigene Schema-Welt.
///
/// Mapping-Konventionen (an Serilog.Sinks.OpenTelemetry angelehnt):
///  - Body = gerenderte Message; Template zusätzlich als Attr <c>serilog.template</c>
///  - Properties → OTLP-Attribute (Scalars/Sequences/Structures wie OTLP sie trägt;
///    Arrays/Kvlists werden downstream zu kanonischen JSON-Strings)
///  - Exception → Semantic Conventions (exception.type/message/stacktrace)
///  - Property "SourceContext" → Scope-Name (wie beim Heimdall.Sdk-Exporter die
///    CategoryName), aus den Attrs entfernt
///
/// Dokumentierte Restverluste (OTLP-Skalar-Set ist kleiner als Serilogs):
///  - decimal → double (Präzision), ulong &gt; long.Max → String, Guid/DateTime/
///    DateTimeOffset/TimeSpan → String (invariant), null-Scalar → Attribut entfällt
///  - TraceId/SpanId werden nicht gemappt (Serilog hat kein natives Trace-Context-
///    Feld; Trace-Korrelation läuft über die OTel-SDK-Pfade)
/// </summary>
internal static class OtlpLogRequestBuilder
{
    private const string TemplateAttr = "serilog.template";
    private const string SourceContextProp = "SourceContext";

    private const string ExceptionTypeAttr = "exception.type";
    private const string ExceptionMessageAttr = "exception.message";
    private const string ExceptionStacktraceAttr = "exception.stacktrace";

    public static ExportLogsServiceRequest Build(IReadOnlyCollection<LogEvent> batch, HeimdallSinkOptions options)
    {
        var request = new ExportLogsServiceRequest();
        if (batch is null || batch.Count == 0) return request;

        var resource = BuildResource(options);

        // Records nach Scope gruppieren (ScopeLogs-Gruppe je SourceContext);
        // ohne SourceContext landen sie in einer Scope-losen Gruppe.
        Dictionary<string, List<LogEvent>>? byScope = null;
        List<LogEvent>? scopeless = null;
        foreach (var e in batch)
        {
            var scope = e.Properties.TryGetValue(SourceContextProp, out var v)
                        && v is ScalarValue { Value: string ctx } ? ctx : null;
            if (scope is null)
            {
                scopeless ??= new List<LogEvent>();
                scopeless.Add(e);
            }
            else
            {
                byScope ??= new Dictionary<string, List<LogEvent>>(StringComparer.Ordinal);
                if (!byScope.TryGetValue(scope, out var list))
                    byScope[scope] = list = new List<LogEvent>();
                list.Add(e);
            }
        }

        var rl = new ResourceLogs { Resource = resource };

        if (scopeless is not null)
        {
            var sl = new ScopeLogs();
            foreach (var e in scopeless) sl.LogRecords.Add(ToLogRecord(e));
            if (sl.LogRecords.Count > 0) rl.ScopeLogs.Add(sl);
        }

        if (byScope is not null)
        {
            foreach (var (name, events) in byScope)
            {
                var sl = new ScopeLogs { Scope = new InstrumentationScope { Name = name } };
                foreach (var e in events) sl.LogRecords.Add(ToLogRecord(e));
                rl.ScopeLogs.Add(sl);
            }
        }

        if (rl.ScopeLogs.Count > 0) request.ResourceLogs.Add(rl);
        return request;
    }

    private static Resource BuildResource(HeimdallSinkOptions options)
    {
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue
        {
            Key = "service.name",
            Value = new AnyValue { StringValue = string.IsNullOrWhiteSpace(options.ServiceName) ? "unknown" : options.ServiceName },
        });
        if (!string.IsNullOrWhiteSpace(options.ServiceVersion))
            resource.Attributes.Add(new KeyValue { Key = "service.version", Value = new AnyValue { StringValue = options.ServiceVersion } });
        if (options.ResourceAttributes is not null)
        {
            foreach (var kv in options.ResourceAttributes)
            {
                var v = MapScalar(kv.Value);
                if (v is not null) resource.Attributes.Add(new KeyValue { Key = kv.Key, Value = v });
            }
        }
        return resource;
    }

    private static LogRecord ToLogRecord(LogEvent e)
    {
        var rec = new LogRecord
        {
            TimeUnixNano = ToUnixNano(e.Timestamp),
            ObservedTimeUnixNano = ToUnixNano(e.Timestamp),
            SeverityNumber = MapSeverity(e.Level),
            SeverityText = SeverityText(e.Level),
        };

        var rendered = e.RenderMessage();
        if (!string.IsNullOrEmpty(rendered)) rec.Body = new AnyValue { StringValue = rendered };
        rec.Attributes.Add(new KeyValue { Key = TemplateAttr, Value = new AnyValue { StringValue = e.MessageTemplate.Text } });

        if (e.Exception is not null)
        {
            rec.Attributes.Add(new KeyValue { Key = ExceptionTypeAttr, Value = new AnyValue { StringValue = e.Exception.GetType().FullName ?? e.Exception.GetType().Name } });
            if (e.Exception.Message is { Length: > 0 } msg)
                rec.Attributes.Add(new KeyValue { Key = ExceptionMessageAttr, Value = new AnyValue { StringValue = msg } });
            rec.Attributes.Add(new KeyValue { Key = ExceptionStacktraceAttr, Value = new AnyValue { StringValue = e.Exception.ToString() } });
        }

        foreach (var prop in e.Properties)
        {
            // SourceContext ist zum Scope-Namen geworden → nicht doppelt als Attr.
            if (prop.Key == SourceContextProp) continue;
            var v = MapValue(prop.Value);
            if (v is not null) rec.Attributes.Add(new KeyValue { Key = prop.Key, Value = v });
        }

        return rec;
    }

    /// <summary>Serilog-Level → OTel SeverityNumber (gleiche Bänder wie Heimdall: 1/5/9/13/17/21).</summary>
    private static SeverityNumber MapSeverity(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => SeverityNumber.Trace,
        LogEventLevel.Debug => SeverityNumber.Debug,
        LogEventLevel.Information => SeverityNumber.Info,
        LogEventLevel.Warning => SeverityNumber.Warn,
        LogEventLevel.Error => SeverityNumber.Error,
        _ => SeverityNumber.Fatal,
    };

    private static string SeverityText(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "TRACE",
        LogEventLevel.Debug => "DEBUG",
        LogEventLevel.Information => "INFO",
        LogEventLevel.Warning => "WARN",
        LogEventLevel.Error => "ERROR",
        _ => "FATAL",
    };

    private static ulong ToUnixNano(DateTimeOffset ts) => (ulong)(ts.UtcTicks * 100);

    // ---------------------------------------------------------------------
    // Wert-Mapping: LogEventPropertyValue → AnyValue
    // ---------------------------------------------------------------------

    private static AnyValue? MapValue(LogEventPropertyValue? v) => v switch
    {
        ScalarValue s => MapScalar(s.Value),
        SequenceValue seq => MapSequence(seq),
        StructureValue st => MapStructure(st),
        DictionaryValue d => MapDictionary(d),
        _ => v is null ? null : new AnyValue { StringValue = v.ToString() },
    };

    private static AnyValue? MapScalar(object? value) => value switch
    {
        null => null,   // OTLP hat keine Null-Variante → Attribut entfällt (dokumentiert)
        string s => new AnyValue { StringValue = s },
        char c => new AnyValue { StringValue = new string(c, 1) },
        bool b => new AnyValue { BoolValue = b },
        byte or sbyte or short or ushort or int or uint or long => new AnyValue { IntValue = Convert.ToInt64(value) },
        ulong ul => (long)ul < 0
            ? new AnyValue { StringValue = ul.ToString(CultureInfo.InvariantCulture) }  // Overflow → String (dokumentiert)
            : new AnyValue { IntValue = (long)ul },
        float or double => new AnyValue { DoubleValue = Convert.ToDouble(value) },
        decimal dec => new AnyValue { DoubleValue = (double)dec },   // Präzisionsverlust (dokumentiert)
        Guid g => new AnyValue { StringValue = g.ToString() },
        DateTimeOffset dto => new AnyValue { StringValue = dto.ToString("O", CultureInfo.InvariantCulture) },
        DateTime dt => new AnyValue { StringValue = dt.ToString("O", CultureInfo.InvariantCulture) },
        TimeSpan ts => new AnyValue { StringValue = ts.ToString("c", CultureInfo.InvariantCulture) },
        _ => new AnyValue { StringValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty },
    };

    private static AnyValue MapSequence(SequenceValue seq)
    {
        var arr = new ArrayValue();
        foreach (var item in seq.Elements)
        {
            // Null-Elemente bleiben als leere AnyValue positionstreu erhalten.
            arr.Values.Add(MapValue(item) ?? new AnyValue());
        }
        return new AnyValue { ArrayValue = arr };
    }

    private static AnyValue MapStructure(StructureValue st)
    {
        var kv = new KeyValueList();
        foreach (var p in st.Properties)
        {
            var v = MapValue(p.Value);
            if (v is not null) kv.Values.Add(new KeyValue { Key = p.Name, Value = v });
        }
        return new AnyValue { KvlistValue = kv };
    }

    private static AnyValue MapDictionary(DictionaryValue d)
    {
        var kv = new KeyValueList();
        foreach (var entry in d.Elements)
        {
            var key = entry.Key.Value switch
            {
                string s => s,
                null => string.Empty,
                var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty,
            };
            var v = MapValue(entry.Value);
            if (v is not null && key.Length > 0) kv.Values.Add(new KeyValue { Key = key, Value = v });
        }
        return new AnyValue { KvlistValue = kv };
    }
}