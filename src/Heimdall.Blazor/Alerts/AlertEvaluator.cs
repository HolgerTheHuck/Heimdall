using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall;
using Heimdall.Prometheus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// AlertEvaluator — getakteter Singleton (IHostedService + Timer). Evaluiert
// alle aktivierten Regeln im Takt (EvaluationIntervalSeconds), treibt den
// Zustandsautomat (Ok→Pending→Firing→Resolved) und benachrichtigt Kanäle bei
// Übergängen (Firing/Resolved). Dedup: waehrend Firing kein Re-Notify.
//
// Timer-in-Singleton-Konvention (wie Retention-Sweeper SQLiteTelemetrySink),
// ABER IHostedService fuer sauberes Start-on-Boot / Stop-on-Shutdown:
// StartAsync startet den Timer, StopAsync disposet ihn.
//
// Reentrancy-Guard via Interlocked — ein laengerer Tick (langsamer SMTP/
// Webhook im fire-and-forget zwar entkoppelt, aber EvalOnce selbst kann bei
// vielen Regeln dauern) ueberspringt den naechsten Tick statt zu stapeln.
// ---------------------------------------------------------------------------

/// <summary>
/// Getakteter Alarm-Evaluator. Wird via <see cref="AddHeimdallAlerting"/>
/// registriert (Singleton + HostedService). Pure Kernlogik (EvalCondition/
/// Transition) ist internal fuer Unit-Tests ohne HTTP-Stack.
/// </summary>
internal sealed class AlertEvaluator : IHostedService, IDisposable
{
    private readonly IHeimdallQuery _query;
    private readonly IAlertRuleStore _ruleStore;
    private readonly IAlertStateStore _stateStore;
    private readonly IReadOnlyDictionary<string, IAlertChannel> _channels;
    private readonly PromEngine? _engine;
    private readonly ILogger<AlertEvaluator> _logger;
    private readonly HeimdallAlertingOptions _opts;
    private readonly Timer _timer;
    private int _busy;
    // Backpressure-Limit für fire-and-forget-Notify: verhindert, dass bei
    // vielen feuernden Regeln mit langsamen Channels (SMTP/Webhook) beliebig
    // viele Requests im Flight sind (Resourcen-Exhaustion, SMTP-Verbindungs-
    // Limits). SemaphoreSlim mit konfigurierbarem Cap; bei Overflow wird
    // der Channel im aktuellen Tick übersprungen + geloggt (nicht blockiert,
    // da der Eval-Loop sonst auf Channel-Latenz hängen würde).
    private readonly SemaphoreSlim _notifyGate = new(Math.Max(1, 16), Math.Max(1, 16));

    public AlertEvaluator(
        IHeimdallQuery query,
        IAlertRuleStore ruleStore,
        IAlertStateStore stateStore,
        IEnumerable<IAlertChannel> channels,
        PromEngine? engine,
        ILogger<AlertEvaluator> logger,
        HeimdallAlertingOptions opts)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _channels = (channels ?? Enumerable.Empty<IAlertChannel>())
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _engine = engine;
        _logger = logger;
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        var period = Math.Max(1, _opts.EvaluationIntervalSeconds) * 1000L;
        _timer = new Timer(_ => _ = EvalTick(), null, Timeout.Infinite, Timeout.Infinite);
        // Period-Feld fuer Tests/Stopp; DueTime wird in StartAsync gesetzt.
        _periodMs = period;
    }

    private readonly long _periodMs;

    // === IHostedService ====================================================
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(_periodMs));
        _logger.LogInformation("AlertEvaluator gestartet (Takt {Period}s, {Channels} Kanäle).",
            _opts.EvaluationIntervalSeconds, _channels.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        // Lauenden Tick abwarten (Reentrancy-Guard im EvalTick setzt _busy zurück).
        // Verhindert, dass ein Tick im Flight nach Host-Shutdown noch Channel-
        // Notifications feuert (z. B. SMTP-Timeout nach Dispose). Mit Timeout,
        // damit ein hängender Tick nicht den Shutdown blockt.
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (Interlocked.CompareExchange(ref _busy, 0, 0) != 0)
                await Task.Delay(50, stopCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Shutdown-Timeout: Tick hart abbrechen */ }
        _logger.LogInformation("AlertEvaluator gestoppt.");
    }

    public void Dispose()
    {
        _timer.Dispose();
        _notifyGate.Dispose();
    }

    // === Tick-Schleife ======================================================
    /// <summary>Timer-Callback: Reentrancy-Guard, dann async EvalOnce.</summary>
    private async Task EvalTick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // voriger Tick laeuft noch
        try { await EvalOnce().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "AlertEvaluator-Tick fehlgeschlagen."); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Evaluiert alle Regeln einmal. Internal fuer Tests.</summary>
    internal async Task EvalOnce()
    {
        var refs = _ruleStore.List();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nowNano = nowMs * 1_000_000L;
        foreach (var rf in refs)
        {
            var rule = _ruleStore.Get(rf.Id);
            if (rule is null) continue;
            try
            {
                await ProcessRule(rule, nowMs, nowNano).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogError(ex, "Auswertung von Regel {Rule} fehlgeschlagen.", rule.Name); }
        }
    }

    /// <summary>Evaluiert EINE Regel: Bedingung → Zustandsautomat → persistieren → notify.</summary>
    internal async Task ProcessRule(AlertRule rule, long nowMs, long nowNano)
    {
        var current = _stateStore.Get(rule.Id);
        // Per-Regel-Takt: EvalIntervalSeconds>0 überspringt Tick, bis das
        // pro-Regel-Intervall seit LastEval verstrichen ist (0 = globaler Takt,
        // immer evaluieren — Abwärtskompatibilität).
        if (rule.EvalIntervalSeconds > 0 && current is { LastEvalUnixMs: var last } && (nowMs - last) < rule.EvalIntervalSeconds * 1000L)
            return;
        if (!rule.Enabled)
        {
            // Deaktivierte Regel → Zustand Ok (aufraeumen falls vorher aktiv).
            // Wenn die Regel vorher Firing war, notifyResolve, damit Empfänger
            // nicht im Alarm hängen bleiben (vorher wurde Resolved bei Disable
            // verschluckt — Alarm-Receiver hatten kein Entwarnungs-Event).
            if (current is { State: AlertState.Firing } firingCurrent)
            {
                var resolved = current! with { State = AlertState.Resolved, SinceUnixMs = nowMs, LastEvalUnixMs = nowMs };
                _stateStore.Put(resolved);
                await NotifyAsync(rule, resolved, AlertState.Resolved, firingCurrent.LastValue ?? 0, "Regel deaktiviert", nowMs).ConfigureAwait(false);
            }
            else if (current is { State: not AlertState.Ok })
            {
                _stateStore.Put(current with { State = AlertState.Ok, SinceUnixMs = nowMs, LastEvalUnixMs = nowMs });
            }
            return;
        }

        var (firing, value, msg) = EvalCondition(rule, nowMs, nowNano);
        var next = Transition(rule, current, firing, value, msg, nowMs, out var notifyFire, out var notifyResolve);
        next = next with { LastEvalUnixMs = nowMs, LastValue = firing ? value : (current?.LastValue ?? value) };

        // LastEvalUnixMs/State/Value immer persistieren (Zustand + Diagnose aktuell halten).
        _stateStore.Put(next);

        if (notifyFire) await NotifyAsync(rule, next, AlertState.Firing, value, msg, nowMs).ConfigureAwait(false);
        if (notifyResolve) await NotifyAsync(rule, next, AlertState.Resolved, value, msg, nowMs).ConfigureAwait(false);
    }

    // === Bedingung pro Signal ==============================================
    /// <summary>
    /// Wertet die Bedingung einer Regel aus. Liefert (feuert, lastValue, message).
    /// Pure (keine Persistenz) — internal fuer Tests.
    /// </summary>
    internal (bool firing, double value, string? message) EvalCondition(AlertRule rule, long nowMs, long nowNano)
    {
        switch (rule.Signal)
        {
            case AlertSignal.Metric:
                return EvalMetric(rule, nowMs);
            case AlertSignal.Log:
                return EvalLog(rule, nowNano);
            case AlertSignal.Trace:
                return EvalTrace(rule, nowNano);
            default:
                return (false, 0, null);
        }
    }

    private (bool, double, string?) EvalMetric(AlertRule rule, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(rule.Promql)) return (false, 0, "Kein PromQL");
        if (_engine is null)
        {
            // Prometheus nicht aktiviert → Metrik-Regeln can't evaluate. Kein per-Tick-
            // Rauschen; der Hinweis landet im Zustand (Detail-Seite zeigt ihn).
            return (false, 0, "PromEngine nicht verfügbar");
        }
        try
        {
            var result = _engine.EvalInstant(rule.Promql, nowMs);
            if (result.Kind == PromResultKind.Vector && result.Vector is { Samples: { Count: > 0 } samples })
            {
                // Vorher wurde nur samples[0] ausgewertet — bei Multi-Serie-PromQL
                // (z. B. `rate(...)[5m] by (job)`) entschied die Reihenfolge, nicht
                // die Semantik. Jetzt feuert, wenn IRGENDEINE Serie einen Wert
                // ungleich 0+NaN hat (PromQL-Vergleiche feuern per Serie mit 1/0);
                // als Value nehmen wir das Maximum (konservativ: der alarmierende
                // Peak ist diagnostisch relevanter als der erste Zufallstreffer).
                double max = double.NaN;
                int firing = 0;
                foreach (var s in samples)
                {
                    var v = s.Value;
                    if (!double.IsNaN(v) && v != 0) { firing++; if (double.IsNaN(max) || v > max) max = v; }
                }
                if (firing > 0)
                    return (true, double.IsNaN(max) ? samples[0].Value : max, $"{firing}/{samples.Count} Treffer-Serie(n)");
                return (false, 0, $"{samples.Count} Serie(n), keine feuert");
            }
            if (result.Kind == PromResultKind.Scalar && result.Scalar is { } sc && !double.IsNaN(sc.Value) && sc.Value != 0)
                return (true, sc.Value, "Skalar-Treffer");
            return (false, 0, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PromQL-Auswertung fehlgeschlagen fuer {Rule}: {Expr}", rule.Name, rule.Promql);
            return (false, 0, "PromQL-Fehler: " + ex.Message);
        }
    }

    private (bool, double, string?) EvalLog(AlertRule rule, long nowNano)
    {
        var windowNs = Math.Max(1, rule.WindowSeconds) * 1_000_000_000L;
        var limit = Math.Max(1, rule.Threshold + 1);
        var search = new LogSearch
        {
            Text = rule.LogText,
            MinSeverity = rule.MinSeverity,
            FromUnixNano = nowNano - windowNs,
            ToUnixNano = nowNano,
            Limit = limit,
        };
        var rows = _query.SearchLogs(search);
        var count = rows.Count;
        return (count > rule.Threshold, count, count > 0 ? $"{count} Logs" : null);
    }

    private (bool, double, string?) EvalTrace(AlertRule rule, long nowNano)
    {
        var windowNs = Math.Max(1, rule.WindowSeconds) * 1_000_000_000L;
        var limit = Math.Max(1, rule.Threshold + 1);
        var filter = new TraceFilter
        {
            HasError = rule.HasError,
            ServiceName = rule.ServiceName,
            NameContains = rule.NameContains,
            FromUnixNano = nowNano - windowNs,
            ToUnixNano = nowNano,
            Limit = limit,
        };
        var traces = _query.ListTraces(filter);
        var count = traces.Count;
        return (count > rule.Threshold, count, count > 0 ? $"{count} Traces" : null);
    }

    // === Zustandsautomat ===================================================
    /// <summary>
    /// Reiner Zustandsuebergang. Liefert den neuen AlertEvent und signalisiert
    /// ob Firing-/Resolved-Benachrichtigungen gesendet werden sollen.
    /// Internal fuer Tests (keine Persistenz, kein Notify).
    /// </summary>
    internal static AlertEvent Transition(
        AlertRule rule, AlertEvent? current, bool firing, double value, string? msg, long nowMs,
        out bool notifyFire, out bool notifyResolve)
    {
        notifyFire = false;
        notifyResolve = false;
        var state = current?.State ?? AlertState.Ok;
        var since = current?.SinceUnixMs ?? nowMs;
        var lastNotified = current?.LastNotifiedUnixMs;

        switch (state)
        {
            case AlertState.Ok:
                if (firing) return new AlertEvent(rule.Id, AlertState.Pending, nowMs, lastNotified, value, msg, nowMs);
                return new AlertEvent(rule.Id, AlertState.Ok, since, lastNotified, current?.LastValue, current?.Message, nowMs);

            case AlertState.Pending:
                if (firing)
                {
                    if (nowMs - since >= rule.ForSeconds * 1000L)
                    {
                        notifyFire = true;
                        return new AlertEvent(rule.Id, AlertState.Firing, since, nowMs, value, msg, nowMs);
                    }
                    return new AlertEvent(rule.Id, AlertState.Pending, since, lastNotified, value, msg, nowMs);
                }
                // Bedingung weggefallen, noch vor Firing → direkt Ok (nicht benachrichtigt).
                return new AlertEvent(rule.Id, AlertState.Ok, nowMs, null, value, msg, nowMs);

            case AlertState.Firing:
                if (firing)
                {
                    // Dedup: kein Re-Notify waehrend Firing.
                    return new AlertEvent(rule.Id, AlertState.Firing, since, lastNotified, value, msg, nowMs);
                }
                notifyResolve = lastNotified.HasValue;   // nur benachrichtigen, wenn vorher Firing gemeldet
                return new AlertEvent(rule.Id, AlertState.Resolved, nowMs, lastNotified, value, msg, nowMs);

            case AlertState.Resolved:
                if (firing) return new AlertEvent(rule.Id, AlertState.Pending, nowMs, null, value, msg, nowMs);
                return new AlertEvent(rule.Id, AlertState.Ok, nowMs, null, value, msg, nowMs);
        }
        return new AlertEvent(rule.Id, AlertState.Ok, since, lastNotified, value, msg, nowMs);
    }

    // === Benachrichtigung ==================================================
    private Task NotifyAsync(AlertRule rule, AlertEvent ev, AlertState notifyState, double value, string? msg, long nowMs)
    {
        if (rule.Channels is null || rule.Channels.Count == 0) return Task.CompletedTask;
        var notification = new AlertNotification(rule.Name, rule.Signal, notifyState, value, msg, nowMs, rule.Id, "");
        foreach (var name in rule.Channels)
        {
            if (!_channels.TryGetValue(name, out var channel))
            {
                _logger.LogWarning("Kanal {Channel} fuer Regel {Rule} nicht gefunden — uebersprungen.", name, rule.Name);
                continue;
            }
            // Backpressure: begrenzt gleichzeitige in-flight Notify-Tasks über
            // _notifyGate. Bei vollem Gate (z. B. viele feuernde Regeln + langsamer
            // SMTP) wird der Channel im aktuellen Tick übersprungen + geloggt,
            // statt den Eval-Loop zu blocken oder beliebig viele Requests zu
            // stapeln. Der nächste Tick retry automatisch (Zustandsautomat).
            if (!_notifyGate.Wait(0))
            {
                _logger.LogWarning("Notify-Gate voll — Kanal {Channel} fuer {Rule} uebersprungen (Backpressure).", name, rule.Name);
                continue;
            }
            _ = channel.SendAsync(notification, CancellationToken.None)
                .ContinueWith(t =>
                {
                    _notifyGate.Release();
                    if (t.IsFaulted) _logger.LogError(t.Exception, "Kanal {Channel} fuer {Rule} fehlgeschlagen.", name, rule.Name);
                }, TaskScheduler.Default);
        }
        return Task.CompletedTask;
    }
}