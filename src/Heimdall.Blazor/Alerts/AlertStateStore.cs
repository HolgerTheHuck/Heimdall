using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Persistenter Zustands-Store fuer Alarmregeln. EINE Datei alertstate.json
// mit einem Dictionary RuleId -> AlertEvent. In-Memory-Cache + Flush bei
// jedem Zustandsuebergang. Haelt Neustart stand (ein Firing-Alert bleibt
// Firing — kein Re-Notify-Spam nach Restart, da SinceUnixMs erhalten ist).
// ---------------------------------------------------------------------------

/// <summary>Vertrag fuer den Alarm-Zustandsspeicher.</summary>
public interface IAlertStateStore
{
    /// <summary>Liefert den Zustand einer Regel oder null (=> implizit Ok).</summary>
    AlertEvent? Get(string ruleId);

    /// <summary>Alle gespeicherten Zustaende (RuleId -> AlertEvent).</summary>
    IReadOnlyDictionary<string, AlertEvent> All();

    /// <summary>Speichert/aktualisiert den Zustand einer Regel (persistiert sofort).</summary>
    void Put(AlertEvent ev);

    /// <summary>Entfernt den Zustand einer Regel (z. B. nach Regel-Loeschung).</summary>
    void Remove(string ruleId);
}

/// <summary>
/// Dateibasierte Implementierung von <see cref="IAlertStateStore"/>. Eine Datei
/// <c>{dir}/alertstate.json</c> mit dem Dictionary RuleId -> AlertEvent.
/// </summary>
public sealed class FileAlertStateStore : IAlertStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, AlertEvent> _cache;

    public FileAlertStateStore(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("Verzeichnis fehlt", nameof(dir));
        _path = Path.Combine(dir, "alertstate.json");
        _cache = Load();
    }

    /// <inheritdoc />
    public AlertEvent? Get(string ruleId)
    {
        lock (_gate)
        {
            return ruleId is not null && _cache.TryGetValue(ruleId, out var ev) ? ev : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, AlertEvent> All()
    {
        lock (_gate)
        {
            return new Dictionary<string, AlertEvent>(_cache, StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public void Put(AlertEvent ev)
    {
        if (ev is null || string.IsNullOrEmpty(ev.RuleId)) return;
        lock (_gate)
        {
            _cache[ev.RuleId] = ev;
            Flush();
        }
    }

    /// <inheritdoc />
    public void Remove(string ruleId)
    {
        if (string.IsNullOrEmpty(ruleId)) return;
        lock (_gate)
        {
            if (_cache.Remove(ruleId)) Flush();
        }
    }

    private Dictionary<string, AlertEvent> Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(_path)) return new Dictionary<string, AlertEvent>(StringComparer.Ordinal);
            var raw = File.ReadAllText(_path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, AlertEvent>>(raw, JsonOpts);
            return dict ?? new Dictionary<string, AlertEvent>(StringComparer.Ordinal);
        }
        catch { /* kaputter Zustand → frisch starten, Alerts evaluieren neu */ }
        return new Dictionary<string, AlertEvent>(StringComparer.Ordinal);
    }

    private void Flush()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_cache, JsonOpts));
        }
        catch { /* Persistenz ist Best-Effort — In-Memory-Zustand bleibt aktuell */ }
    }
}