using System;
using System.Collections.Generic;

namespace Heimdall;

// ---------------------------------------------------------------------------
// Heimdall-Interfaces. Das ist DIE Schnittstelle, die alle Verbraucher
// (auch Walhalla selbst) referenzieren. Keine Abhaengigkeiten, F#-freundlich
// (nur Interfaces + Records, keine C#-Only-Features).
// ---------------------------------------------------------------------------

/// <summary>Ein aktiver Span; durch Dispose/End abgeschlossen.</summary>
public interface IHeimdallSpan : IDisposable
{
    /// <summary>Trace- und Span-ID des erzeugten Spans ( gesetzt erst nach End).</summary>
    byte[] TraceId { get; }
    byte[] SpanId { get; }

    void SetAttribute(string key, object? value);
    void AddEvent(string name, params HAttribute[] attributes);
    void SetStatus(HStatusCode code, string? message = null);
    /// <summary>Schliesst den Span ab (Dauer stoppen, an Sink uebergeben). Idempotent.</summary>
    void End();
}

/// <summary>Tracer erzeugt Spans. Eine Instanz pro Instrumentation-Scope.</summary>
public interface IHeimdallTracer
{
    IHeimdallSpan StartSpan(string name, HSpanKind kind = HSpanKind.Internal, IHeimdallSpan? parent = null);
}

/// <summary>Logger emittiert Log-Eintraege.</summary>
public interface IHeimdallLogger
{
    void Emit(HSeverity severity, string? body, params HAttribute[] attributes);
}

/// <summary>Meter erzeugt Metrik-Instrumente.</summary>
public interface IHeimdallMeter
{
    IHeimdallCounter CreateCounter(string name, string? unit = null);
    IHeimdallHistogram CreateHistogram(string name, string? unit = null);
    IHeimdallGauge CreateGauge(string name, string? unit = null);
}

/// <summary>Monotoner Zaehler (Sum, delta oder cumulative).</summary>
public interface IHeimdallCounter
{
    void Add(double value, params HAttribute[] attributes);
}

/// <summary>Histogramm (Buckets werden aus einer konfigurierten Bound-Liste gebildet).</summary>
public interface IHeimdallHistogram
{
    void Record(double value, params HAttribute[] attributes);
}

/// <summary>Gauge (aktueller Wert, ueberschreibbar).</summary>
public interface IHeimdallGauge
{
    void Set(double value, params HAttribute[] attributes);
}

/// <summary>
/// Ablageziel fuer fertig aufgebaute Model-Records. Implementiert vom Storage
/// (Walhalla) bzw. von einem In-Memory-Buffer. Das ist die einzige Schreibstelle,
/// die alle drei Ingestion-Pfade (Sdk/Direct/OTLP) gemeinsam haben.
/// </summary>
public interface IHeimdallSink
{
    void WriteSpans(IReadOnlyList<HSpan> spans);
    void WriteLogs(IReadOnlyList<HLogRecord> logs);
    void WriteMetrics(IReadOnlyList<HMetricPoint> metrics);
}

/// <summary>
/// Einstieg: liefert pro Scope einen Tracer/Logger/Meter. Implementiert von
/// Heimdall.Direct (native API), Heimdall.Sdk (OTel-Exporter) bzw. Noop.
/// Verbraucher halten typischerweise eine IHeimdallHub-Instanz (DI).
/// </summary>
public interface IHeimdallHub
{
    IHeimdallTracer GetTracer(string name, string? version = null);
    IHeimdallLogger GetLogger(string name, string? version = null);
    IHeimdallMeter GetMeter(string name, string? version = null);
}