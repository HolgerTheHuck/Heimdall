using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.MvcSample.Store;

// --- Domäne -------------------------------------------------------------

public sealed record Kunde(int Id, string Name, string Email);
public sealed record Adresse(int Id, int KundeId, string Strasse, string Stadt, string Plz);
public sealed record Artikel(int Id, string Name, decimal Preis, int Lager);
public sealed record BestellPosition(int ArtikelId, int Menge);
public sealed record Bestellung(int Id, int KundeId, int AdresseId,
    List<BestellPosition> Positionen, decimal Gesamt, string Status);

/// <summary>
/// In-Memory-Datenbestand für das MvcSample (keine echte DB — die „Datenbank" ist
/// nur eine simulierte Latenz, damit die Heimdall-Antwortzeit-Messung etwas zu
/// sehen bekommt). Thread-sicher via Lock; <see cref="Random"/> ist nicht
/// thread-sicher, deshalb hinter demselben Lock.
/// </summary>
public sealed class DataStore : IDisposable
{
    private readonly object _lock = new();
    private readonly Random _rng = new();
    private int _nextKunde, _nextAdresse, _nextArtikel, _nextBestellung;

    public List<Kunde> Kunden { get; } = new();
    public List<Adresse> Adressen { get; } = new();
    public List<Artikel> Artikel { get; } = new();
    public List<Bestellung> Bestellungen { get; } = new();

    public DataStore()
    {
        // Saat-Bestand, damit GET-Listen sofort etwas liefern.
        for (int i = 1; i <= 5; i++)
        {
            Kunden.Add(new Kunde(i, $"Kunde {i}", $"k{i}@example.com"));
            Adressen.Add(new Adresse(i, i, $"Str{i}", $"Stadt{i}", "10" + i));
            Artikel.Add(new Artikel(i, $"Artikel {i}", 9.90m * i, 100 - i));
        }
        _nextKunde = 6; _nextAdresse = 6; _nextArtikel = 6; _nextBestellung = 1;
    }

    public int NextKunde() { lock (_lock) return _nextKunde++; }
    public int NextAdresse() { lock (_lock) return _nextAdresse++; }
    public int NextArtikel() { lock (_lock) return _nextArtikel++; }
    public int NextBestellung() { lock (_lock) return _nextBestellung++; }

    /// <summary>Simuliert DB-Latenz passend zur Operation; gelegentlich ein
    /// Ausreißer (langsamer Query) → sichtbares p95/p99.</summary>
    public async Task LatencyAsync(string kind, CancellationToken ct)
    {
        int ms = kind switch
        {
            "list"   => 6  + RngInt(0, 18),
            "get"    => 3  + RngInt(0, 10),
            "insert" => 10 + RngInt(0, 35),
            "update" => 12 + RngInt(0, 55),
            "delete" => 8  + RngInt(0, 25),
            "order"  => 30 + RngInt(0, 120),
            _        => 5,
        };
        if (RngDouble() < 0.05) ms += 180 + RngInt(0, 260);   // ~5 % langsam
        await Task.Delay(ms, ct);
    }

    /// <summary>~2 % der Aufrufe „fehlschlagen" → sichtbare Fehlerrate (5xx).</summary>
    public bool MaybeFault() => RngDouble() < 0.02;

    private int RngInt(int min, int max)
    {
        lock (_lock) return _rng.Next(min, max);
    }
    private double RngDouble()
    {
        lock (_lock) return _rng.NextDouble();
    }

    public void Dispose() { }
}