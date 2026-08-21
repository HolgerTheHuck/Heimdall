using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Dateibasierte Persistenz fuer Alarmregeln. Jede Regel liegt als
// {dir}/{id}.json auf der Platte und ueberlebt einen Neustart. 1:1 an
// FileGrafanaDashboardStore gespiegelt: SafeName-Whitelist gegen Pfad-
// Traversierung, lock(_gate), EnsureDir, lenient List() ueberspringt
// kaputte Dateien. Serialisierung via JsonSerializer (POCO-Schema).
// ---------------------------------------------------------------------------

/// <summary>Persistenzvertrag fuer Alarmregeln.</summary>
public interface IAlertRuleStore
{
    /// <summary>Alle Regeln als Listen-Eintraege (Id/Name/Signal/Enabled), sortiert nach Name.</summary>
    IReadOnlyList<AlertRuleRef> List();

    /// <summary>Liefert die vollstaendige Regel zur Id oder null.</summary>
    AlertRule? Get(string id);

    /// <summary>Speichert die Regel (generiert eine Id falls leer) und liefert die Id.</summary>
    string Save(AlertRule rule);

    /// <summary>Loescht die Regel zur Id (no-op, falls nicht vorhanden).</summary>
    void Delete(string id);
}

/// <summary>
/// Dateibasierte Implementierung von <see cref="IAlertRuleStore"/>. Jede Regel
/// wird unter <c>{dir}/{id}.json</c> abgelegt. Dateinamen werden auf sichere
/// Zeichen reduziert, sodass Regel-Ids mit Sonderzeichen keine Pfad-Traversierung
/// ermoeglichen.
/// </summary>
public sealed class FileAlertRuleStore : IAlertRuleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly object _gate = new();

    public FileAlertRuleStore(string dir)
    {
        _dir = dir ?? throw new ArgumentNullException(nameof(dir));
    }

    /// <inheritdoc />
    public IReadOnlyList<AlertRuleRef> List()
    {
        EnsureDir();
        var refs = new List<AlertRuleRef>();
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var rule = ReadRule(path);
                if (rule is not null)
                    refs.Add(new AlertRuleRef(rule.Id, rule.Name, rule.Signal, rule.Enabled));
            }
            catch { /* kaputtes File ignorieren, UI laeuft weiter */ }
        }
        return refs.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(r => r.Id, StringComparer.Ordinal)
                   .ToList();
    }

    /// <inheritdoc />
    public AlertRule? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var path = PathFor(id);
        lock (_gate)
        {
            return File.Exists(path) ? ReadRule(path) : null;
        }
    }

    /// <inheritdoc />
    public string Save(AlertRule rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (string.IsNullOrWhiteSpace(rule.Name))
            throw new ArgumentException("Regelname fehlt", nameof(rule));
        EnsureDir();
        var id = string.IsNullOrWhiteSpace(rule.Id) ? NewId() : rule.Id;
        var stored = rule with { Id = id, Channels = rule.Channels ?? AlertRule.EmptyChannels };
        var path = PathFor(id);
        lock (_gate)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(stored, JsonOpts));
        }
        return id;
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var path = PathFor(id);
        lock (_gate)
        {
            if (File.Exists(path)) try { File.Delete(path); } catch { }
        }
    }

    private void EnsureDir()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_dir)) try { Directory.CreateDirectory(_dir); } catch { }
        }
    }

    private string PathFor(string id) => Path.Combine(_dir, SafeName(id) + ".json");

    private AlertRule? ReadRule(string path)
    {
        var raw = TryRead(path);
        return string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<AlertRule>(raw!, JsonOpts);
    }

    // Nur Buchstaben/Ziffern/_-/./~ zulassen; Rest auf '_' gemappt (wie Grafana-Store).
    private static string SafeName(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' || c == '~')
                sb.Append(c);
            else sb.Append('_');
        }
        return sb.Length == 0 ? "rule" : sb.ToString();
    }

    private static string TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null!; }
    }

    // Stabile 32-stellige Hex-Id (Guid ohne Bindestriche) — wird einmal vergeben und
    // danach persistent auf der Platte weitergefuehrt.
    private static string NewId() => Guid.NewGuid().ToString("N");
}