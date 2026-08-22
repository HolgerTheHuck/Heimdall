using System;
using System.Threading;

namespace Heimdall.Otlp;

/// <summary>
/// Admission-Control (Concurrency-Cap) für den OTLP/HTTP-Empfänger (Workstream C1).
/// Schützt den (Single-Connection-)SQLite-Sink vor einer Flut paralleler Export-
/// Requests: begrenzt konkurrierendes Body-Parsing (Speicher) und die Lock-
/// Warteschlange am Sink. <c>maxConcurrent &lt;= 0</c> = unbegrenzt (Noop).
///
/// Nicht-blockierend: bei vollem Cap wird ein Request sofort abgewiesen (HTTP 429),
/// statt ihn in einer Warteschlange zu stauchen (Letzteres würde die Latenz nur
/// in die Lock-Warteschlange verschieben). OTel-Exporter behandeln 429 als
/// Retry-Signal.
/// </summary>
public sealed class OtlpAdmissionLimiter : IDisposable
{
    private readonly SemaphoreSlim? _sem;

    /// <summary>
    /// Legt einen Limiter mit <paramref name="maxConcurrent"/> Plätzen an
    /// (<c>&lt;= 0</c> = unbegrenzt → Noop-Limiter).
    /// </summary>
    public OtlpAdmissionLimiter(int maxConcurrent)
        => _sem = maxConcurrent > 0 ? new SemaphoreSlim(maxConcurrent, maxConcurrent) : null;

    /// <summary>
    /// Nicht-blockierender Admissions-Versuch (<see cref="SemaphoreSlim"/>::Wait(0),
    /// keine Wartezeit). <c>true</c> + freizugebende <paramref name="lease"/> bei
    /// Erfolg (lease null bei unbegrenztem Limiter — nichts freizugeben);
    /// <c>false</c> bei vollem Cap (lease null). <paramref name="lease"/> ist in
    /// einem <c>finally</c> zu disposen.
    /// </summary>
    public bool TryEnter(out IDisposable? lease)
    {
        if (_sem is null) { lease = null; return true; }
        if (_sem.Wait(0)) { lease = new Lease(_sem); return true; }
        lease = null; return false;
    }

    /// <summary>Gibt das interne Semaphore frei (Noop beim unbegrenzten Limiter).</summary>
    public void Dispose() => _sem?.Dispose();

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        public Lease(SemaphoreSlim sem) => _sem = sem;
        public void Dispose()
        {
            try { _sem.Release(); } catch (SemaphoreFullException) { /* doppelt freigegeben — ignorieren */ }
        }
    }
}