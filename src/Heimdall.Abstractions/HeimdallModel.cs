using System.Collections.Generic;

namespace Heimdall;

// ---------------------------------------------------------------------------
// Kanonisches Heimdall-Modell. Entkoppelt das OTLP-Protokoll vom Storage.
// Alle Zeitstempel sind Unix-Nanosekunden (long). Trace/Span-IDs sind rohe
// Byte-Arrays (TraceId = 16 Bytes, SpanId = 8 Bytes, OTel-konform).
// ---------------------------------------------------------------------------

/// <summary>Span-Kind, OTel-konform.</summary>
public enum HSpanKind { Internal = 0, Server = 1, Client = 2, Producer = 3, Consumer = 4 }

/// <summary>Span-Status.</summary>
public enum HStatusCode { Unset = 0, Ok = 1, Error = 2 }

/// <summary>Log-Schweregrad (OTel numeric severity, gerundet auf die Stufen).</summary>
public enum HSeverity
{
    Trace = 1,
    Debug = 5,
    Info = 9,
    Warn = 13,
    Error = 17,
    Fatal = 21
}

/// <summary>Metrik-Art.</summary>
public enum HMetricType { Gauge = 0, Sum = 1, Histogram = 2 }

/// <summary>Temporaalitaet fuer Sum/Histogram.</summary>
public enum HTemporality { Unspecified = 0, Delta = 1, Cumulative = 2 }

/// <summary>Resource (der beobachtete Prozess/Dienst); dedupliziert ueber Attribute-Hash.</summary>
public sealed record HResource(IReadOnlyList<HAttribute> Attributes);

/// <summary>Instrumentation-Scope (Bibliothek/Modul, das die Telemetrie erzeugt).</summary>
public sealed record HScope(string Name, string? Version, IReadOnlyList<HAttribute> Attributes);

/// <summary>Ein Span-Event.</summary>
public sealed record HSpanEvent(long TimeUnixNano, string Name, IReadOnlyList<HAttribute> Attributes);

/// <summary>Ein Span-Link (Querverweis zwischen Traces).</summary>
public sealed record HSpanLink(byte[] TraceId, byte[] SpanId, IReadOnlyList<HAttribute> Attributes);

/// <summary>Ein vollstaendiger Span (Trace-Segment).</summary>
public sealed record HSpan(
    byte[] TraceId,
    byte[] SpanId,
    byte[]? ParentSpanId,
    string Name,
    HSpanKind Kind,
    long StartUnixNano,
    long EndUnixNano,
    HStatusCode StatusCode,
    string? StatusMessage,
    IReadOnlyList<HAttribute> Attributes,
    IReadOnlyList<HSpanEvent> Events,
    IReadOnlyList<HSpanLink> Links,
    HResource? Resource,
    HScope? Scope);

/// <summary>Ein Log-Eintrag.</summary>
public sealed record HLogRecord(
    long TimeUnixNano,
    HSeverity Severity,
    string? SeverityText,
    string? Body,
    byte[]? TraceId,
    byte[]? SpanId,
    IReadOnlyList<HAttribute> Attributes,
    HResource? Resource,
    HScope? Scope);

/// <summary>Ein Metrik-Messpunkt (Gauge/Sum oder ein Histogramm-Punkt).</summary>
public sealed record HMetricPoint(
    string Name,
    string? Unit,
    HMetricType Type,
    HTemporality Temporality,
    long TimeUnixNano,
    double Value,                 // Gauge / Sum(monoton)
    long? Count,                  // Sum(int) / Histogramm-Anzahl
    double? Sum,                  // Histogramm-Summe
    double? Min,                  // Histogramm-Min
    double? Max,                  // Histogramm-Max
    IReadOnlyList<long>? BucketCounts,   // Histogramm-Bucket-Zaehler
    IReadOnlyList<double>? ExplicitBounds, // Histogramm-Bucket-Grenzen
    IReadOnlyList<HAttribute> Attributes,
    HResource? Resource,
    HScope? Scope);