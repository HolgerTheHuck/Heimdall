using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.OtelSample.Store;

// --- Domäne ---------------------------------------------------------------

public sealed record Product(int Id, string Name, decimal Price, int Stock);
public sealed record OrderLine(int ProductId, int Qty);
public sealed record Order(int Id, int CustomerId, List<OrderLine> Lines, decimal Total, string Status);

/// <summary>
/// In-Memory-Datenbestand für das OtelSample. Die „Datenbank" ist eine simulierte
/// Latenz (damit http.server.request.duration-Histogramm etwas zu sehen bekommt),
/// mit gelegentlichen Ausreißern (~5 % langsame Queries → sichtbares p95/p99) und
/// ~3 % simulierten Fehlschlägen (→ 5xx-Anteil im Dashboard). Thread-sicher via
/// Lock; <see cref="Random"/> ist nicht thread-sicher, deshalb hinter demselben Lock.
/// </summary>
public sealed class DataStore
{
    private readonly object _lock = new();
    private readonly Random _rng = new();
    private int _nextOrder;

    public List<Product> Products { get; } = new();
    public List<Order> Orders { get; } = new();

    public DataStore()
    {
        for (int i = 1; i <= 8; i++)
            Products.Add(new Product(i, $"Product {i}", 12.50m * i, 100 - i * 3));
        _nextOrder = 1;
    }

    public int NextOrder() { lock (_lock) return _nextOrder++; }

    /// <summary>Simuliert DB-Latenz passend zur Operation; ~5 % Ausreißer
    /// (langsamer Query) → sichtbares p95/p99 im Dauer-Histogramm.</summary>
    public async Task LatencyAsync(string kind, CancellationToken ct)
    {
        int ms = kind switch
        {
            "list"   => 6  + RngInt(0, 18),
            "get"    => 3  + RngInt(0, 10),
            "insert" => 10 + RngInt(0, 35),
            "order"  => 25 + RngInt(0, 90),
            _        => 5,
        };
        if (RngDouble() < 0.05) ms += 180 + RngInt(0, 320);   // ~5 % langsam
        await Task.Delay(ms, ct);
    }

    /// <summary>~3 % der Aufrufe „fehlschlagen" → sichtbare Fehlerrate (5xx) +
    /// error_type-Label im http.server.request.duration-Histogramm.</summary>
    public bool MaybeFault() => RngDouble() < 0.03;

    private int RngInt(int min, int max) { lock (_lock) return _rng.Next(min, max); }
    private double RngDouble() { lock (_lock) return _rng.NextDouble(); }
}