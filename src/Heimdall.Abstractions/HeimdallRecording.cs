using System;
using System.Threading;

namespace Heimdall;

/// <summary>
/// Rekursionsschutz fuer Selbst-Observability. Verhindert, dass das
/// Aufzeichnen von Telemetrie selbst wieder Telemetrie erzeugt
/// (Feedback-Loop, z. B. wenn der Heimdall-Sink in die beobachtete
/// Walhalla-Engine schreibt und diese sich selbst instrumentiert).
///
/// Producer (z. B. Walhalla-Instrumentation, die NUR dieses Abstractions-Paket
/// referenziert) pruefen <see cref="IsSuppressed"/> VOR dem Start eines Spans
/// bzw. einer Metrik und brechen dann ab (Noop). Der Ingest-Buffer und der
/// Storage-Sink setzen beim Flush/Schreiben die Suppression, damit der
/// instrumentierte Schreibpfad nichts aufzeichnet.
///
/// Lebt bewusst in den Abstractions (nicht in Ingest/Storage), damit jeder
/// Producer ohne zusaetzliche Abhaengigkeit darauf zugreifen kann.
/// </summary>
public static class HeimdallRecording
{
    private static readonly AsyncLocal<bool> _suppressed = new();

    /// <summary>True, wenn im aktuellen Async-Fluss Telemetrie unterdrueckt ist.</summary>
    public static bool IsSuppressed => _suppressed.Value;

    /// <summary>Unterdrueckt Telemetrie fuer den aktuellen Async-Fluss bis Dispose.</summary>
    public static IDisposable SuppressScope()
    {
        var previous = _suppressed.Value;
        _suppressed.Value = true;
        return new Reverter(previous);
    }

    private sealed class Reverter : IDisposable
    {
        private readonly bool _previous;
        private int _disposed;

        public Reverter(bool previous) => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _suppressed.Value = _previous;
        }
    }
}