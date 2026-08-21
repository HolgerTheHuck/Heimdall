using System;
using System.Threading;
using Heimdall;

namespace Heimdall.Direct;

/// <summary>
/// Einbindung der Heimdall-Telemetrie in-process, ohne OTel-SDK. Liefert pro Scope
/// einen <see cref="IHeimdallTracer"/>/<see cref="IHeimdallLogger"/>/<see cref="IHeimdallMeter"/>,
/// deren erzeugte Model-Records direkt in einen <see cref="IHeimdallSink"/> (z. B. den
/// IngestBuffer oder direkt den Storage) geschrieben werden.
///
/// F#-freundlich: nutzbar als `use hub = HeimdallHub.Create(sink)` und
/// `use span = tracer.StartSpan("x")`.
///
/// Rekursionsschutz: alle Erzeugungspfade pruefen <see cref="HeimdallRecording.IsSuppressed"/>
/// und brechen bei Suppression ab (Noop) -> kein Feedback-Loop bei Selbst-Observability.
/// </summary>
public sealed class HeimdallHub : IHeimdallHub, IDisposable
{
    private readonly IHeimdallSink _sink;
    private readonly HResource? _resource;
    private readonly AsyncLocal<HeimdallSpan?> _currentSpan = new();

    public HeimdallHub(IHeimdallSink sink, HResource? resource = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _resource = resource;
    }

    /// <summary>Erzeugt eine Hub-Instanz mit Standard-Ressource (service.name).</summary>
    public static HeimdallHub Create(IHeimdallSink sink, string? serviceName = null, string? serviceVersion = null)
    {
        HResource? res = null;
        if (serviceName is not null || serviceVersion is not null)
        {
            var attrs = new System.Collections.Generic.List<HAttribute>(2);
            if (serviceName is not null) attrs.Add(new HAttribute("service.name", serviceName));
            if (serviceVersion is not null) attrs.Add(new HAttribute("service.version", serviceVersion));
            res = new HResource(attrs);
        }
        return new HeimdallHub(sink, res);
    }

    public IHeimdallSink Sink => _sink;
    public HResource? Resource => _resource;

    public IHeimdallTracer GetTracer(string name, string? version = null)
        => new HeimdallTracer(this, MakeScope(name, version));

    public IHeimdallLogger GetLogger(string name, string? version = null)
        => new HeimdallLogger(this, MakeScope(name, version));

    public IHeimdallMeter GetMeter(string name, string? version = null)
        => new HeimdallMeter(this, MakeScope(name, version));

    internal HScope? MakeScope(string name, string? version)
        => string.IsNullOrEmpty(name) ? null : new HScope(name, version, System.Array.Empty<HAttribute>());

    // --- Span-Kontext (Parent-Verkettung ueber AsyncLocal) -----------------

    internal HeimdallSpan? CurrentSpan
    {
        get => _currentSpan.Value;
        set => _currentSpan.Value = value;
    }

    internal void WriteSpan(HSpan span) => SafeWrite(s => s.WriteSpans(new[] { span }));
    internal void WriteLog(HLogRecord log) => SafeWrite(s => s.WriteLogs(new[] { log }));
    internal void WriteMetric(HMetricPoint m) => SafeWrite(s => s.WriteMetrics(new[] { m }));

    private void SafeWrite(Action<IHeimdallSink> write)
    {
        if (HeimdallRecording.IsSuppressed) return;   // Rekursionsschutz
        try { write(_sink); } catch { /* Sink-Fehler darf Producer nicht killen */ }
    }

    public void Dispose()
    {
        // Aktiver Span wird beim Verwerfen der Hub nicht automatisch beendet;
        // der Aufrufer负责 Span.End/Dispose (F# `use`).
    }
}