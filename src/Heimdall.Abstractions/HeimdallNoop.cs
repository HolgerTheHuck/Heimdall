using System;
using System.Collections.Generic;

namespace Heimdall;

/// <summary>
/// Null-Implementierungen. Default, wenn kein Heimdall-Host aktiv ist
/// (z. B. Walhalla als reine Library ohne Telemetrie-Host). Alle Methoden
/// sind no-ops, keine Allokationen pro Aufruf ausser dem uebergebenen Array.
/// </summary>
public static class HeimdallNoop
{
    public static readonly IHeimdallHub Hub = new NoopHub();
    public static readonly IHeimdallTracer Tracer = new NoopTracer();
    public static readonly IHeimdallLogger Logger = new NoopLogger();
    public static readonly IHeimdallMeter Meter = new NoopMeter();
    public static readonly IHeimdallSink Sink = new NoopSink();

    private sealed class NoopHub : IHeimdallHub
    {
        public IHeimdallTracer GetTracer(string name, string? version = null) => Tracer;
        public IHeimdallLogger GetLogger(string name, string? version = null) => Logger;
        public IHeimdallMeter GetMeter(string name, string? version = null) => Meter;
    }

    private sealed class NoopTracer : IHeimdallTracer
    {
        public IHeimdallSpan StartSpan(string name, HSpanKind kind = HSpanKind.Internal, IHeimdallSpan? parent = null)
            => NoopSpan.Instance;
    }

    private sealed class NoopLogger : IHeimdallLogger
    {
        public void Emit(HSeverity severity, string? body, params HAttribute[] attributes) { }
    }

    private sealed class NoopMeter : IHeimdallMeter
    {
        public IHeimdallCounter CreateCounter(string name, string? unit = null) => NoopCounter.Instance;
        public IHeimdallHistogram CreateHistogram(string name, string? unit = null) => NoopHistogram.Instance;
        public IHeimdallGauge CreateGauge(string name, string? unit = null) => NoopGauge.Instance;
    }

    private sealed class NoopSpan : IHeimdallSpan
    {
        public static readonly NoopSpan Instance = new();
        public byte[] TraceId => Array.Empty<byte>();
        public byte[] SpanId => Array.Empty<byte>();
        public void SetAttribute(string key, object? value) { }
        public void AddEvent(string name, params HAttribute[] attributes) { }
        public void SetStatus(HStatusCode code, string? message = null) { }
        public void End() { }
        public void Dispose() { }
    }

    private sealed class NoopCounter : IHeimdallCounter
    {
        public static readonly NoopCounter Instance = new();
        public void Add(double value, params HAttribute[] attributes) { }
    }

    private sealed class NoopHistogram : IHeimdallHistogram
    {
        public static readonly NoopHistogram Instance = new();
        public void Record(double value, params HAttribute[] attributes) { }
    }

    private sealed class NoopGauge : IHeimdallGauge
    {
        public static readonly NoopGauge Instance = new();
        public void Set(double value, params HAttribute[] attributes) { }
    }

    private sealed class NoopSink : IHeimdallSink
    {
        public void WriteSpans(IReadOnlyList<HSpan> spans) { }
        public void WriteLogs(IReadOnlyList<HLogRecord> logs) { }
        public void WriteMetrics(IReadOnlyList<HMetricPoint> metrics) { }
    }
}