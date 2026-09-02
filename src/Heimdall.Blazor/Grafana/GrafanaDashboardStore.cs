using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Heimdall.Blazor.Grafana;

// ---------------------------------------------------------------------------
// Dateibasierte Persistenz importierter Grafana-Dashboards. Jedes Dashboard
// liegt als {dir}/{uid}.json auf der Platte und ueberlebt einen Neustart.
// Das Verzeichnis wird beim ersten Zugriff angelegt. Die Store-Methoden
// sind synchron und thread-safe (einfache Sperre), da die eingebettete UI
// server-gerendert und ueberschaubar parallel ist.
// ---------------------------------------------------------------------------

/// <summary>Referenz auf ein gespeichertes Dashboard (Listen-Eintrag).</summary>
public sealed record GrafanaDashboardRef(string Uid, string Title, int PanelCount);

/// <summary>
/// Persistenzvertrag fuer importierte Grafana-Dashboards. Die Datei-Implementierung
/// <see cref="FileGrafanaDashboardStore"/> speichert jedes Dashboard unter
/// <c>{dir}/{uid}.json</c>.
/// </summary>
public interface IGrafanaDashboardStore
{
    /// <summary>Alle gespeicherten Dashboards (Uid/Title/Panelzahl).</summary>
    IReadOnlyList<GrafanaDashboardRef> List();
    /// <summary>Liefert das geparste Dashboard zur Uid oder null.</summary>
    GrafanaDashboard? Get(string uid);
    /// <summary>Liefert das rohe JSON zur Uid oder null.</summary>
    string? GetRaw(string uid);
    /// <summary>Speichert das JSON; Uid/Title werden aus dem (geparsten) JSON ermittelt.</summary>
    string Save(string rawJson);
    /// <summary>
    /// Speichert das JSON UNTER DER GEBENEN Uid (unabhaengig vom JSON-Inhalt).
    /// Fuer den Editor: Dashboard-Dateien ohne <c>uid</c>-Feld bekommen im Parser
    /// eine Fallback-Uid aus Titel + Panelzahl — nach einem Panel-Add aendert sich
    /// die Panelzahl und damit die Fallback-Uid, die Datei wuerde wechseln. Die
    /// Uid-ueberladung pinnt den Dateinamen an die Route.
    /// </summary>
    string Save(string uid, string rawJson);
    /// <summary>Loescht das Dashboard zur Uid (no-op, falls nicht vorhanden).</summary>
    void Delete(string uid);
}

/// <summary>
/// Dateibasierte Implementierung von <see cref="IGrafanaDashboardStore"/>. Jedes
/// Dashboard wird unter <c>{dir}/{uid}.json</c> abgelegt. Uid/Title stammen aus dem
/// geparsten JSON; fehlt die <c>uid</c>, erzeugt der Parser eine stabile Fallback-Uid.
/// Dateinamen werden auf sichere Zeichen reduziert, sodass Dashboard-UIDs mit
/// Sonderzeichen keine Pfad-Traversierung ermoeglichen.
/// </summary>
public sealed class FileGrafanaDashboardStore : IGrafanaDashboardStore
{
    private readonly string _dir;
    private readonly object _gate = new();

    /// <summary>Erzeugt den Store im Verzeichnis <paramref name="dir"/> (wird angelegt).</summary>
    public FileGrafanaDashboardStore(string dir)
    {
        _dir = dir ?? throw new ArgumentNullException(nameof(dir));
    }

    /// <inheritdoc />
    public IReadOnlyList<GrafanaDashboardRef> List()
    {
        EnsureDir();
        var refs = new List<GrafanaDashboardRef>();
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var raw = File.ReadAllText(path);
                var dash = GrafanaDashboardModel.Parse(raw);
                if (dash is not null)
                    refs.Add(new GrafanaDashboardRef(dash.Uid, dash.Title, dash.Panels.Count));
            }
            catch { /* kaputtes File ignorieren, UI laeuft weiter */ }
        }
        return refs.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(r => r.Uid, StringComparer.Ordinal)
                   .ToList();
    }

    /// <inheritdoc />
    public GrafanaDashboard? Get(string uid)
    {
        var raw = GetRaw(uid);
        return raw is null ? null : GrafanaDashboardModel.Parse(raw);
    }

    /// <inheritdoc />
    public string? GetRaw(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        var path = PathFor(uid);
        lock (_gate)
        {
            return File.Exists(path) ? TryRead(path) : null;
        }
    }

    /// <inheritdoc />
    public string Save(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) throw new ArgumentException("JSON fehlt", nameof(rawJson));
        var dash = GrafanaDashboardModel.Parse(rawJson)
            ?? throw new ArgumentException("Dashboard-JSON ungueltig", nameof(rawJson));
        return Save(dash.Uid, rawJson);
    }

    /// <inheritdoc />
    public string Save(string uid, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(uid)) throw new ArgumentException("Uid fehlt", nameof(uid));
        if (string.IsNullOrWhiteSpace(rawJson)) throw new ArgumentException("JSON fehlt", nameof(rawJson));
        if (GrafanaDashboardModel.Parse(rawJson) is null)
            throw new ArgumentException("Dashboard-JSON ungueltig", nameof(rawJson));
        EnsureDir();
        var path = PathFor(uid);
        lock (_gate)
        {
            File.WriteAllText(path, rawJson);
        }
        return uid;
    }

    /// <inheritdoc />
    public void Delete(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        var path = PathFor(uid);
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

    private string PathFor(string uid) => Path.Combine(_dir, SafeName(uid) + ".json");

    private static string SafeName(string uid)
    {
        // Nur Buchstaben/Ziffern/_-/./~ zulassen; Rest auf '_' gemappt. Verhindert
        // Pfad-Traversierung durch boeswillige oder exotische Dashboard-UIDs.
        var sb = new System.Text.StringBuilder(uid.Length);
        foreach (var c in uid)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' || c == '~')
                sb.Append(c);
            else sb.Append('_');
        }
        return sb.Length == 0 ? "dashboard" : sb.ToString();
    }

    private static string TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null!; }
    }
}