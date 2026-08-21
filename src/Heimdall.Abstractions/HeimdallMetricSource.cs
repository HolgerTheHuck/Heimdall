using System.Collections.Generic;

namespace Heimdall;

// ---------------------------------------------------------------------------
// Lese-Vertrag fuer die Prometheus-kompatible Schicht (Heimdall.Prometheus).
// Metric-generisch (NICHT Prom-spezifisch): liefert rohe OTel-Metriken inkl.
// bereits geparster Labels (attrs_json + resource_json). Das Prom-Layer macht
// anschliessend Namens-Mapping (Counter -> _total, Histogramm -> _bucket/...
// _sum/_count, service.name -> job) und PromQL-Auswertung.
//
// Wie IHeimdallQuery ist dies ein SEPARATER Vertrag, den beide Storage-Backends
// (Walhalla, SQLite) implementieren. Er erweitert IHeimdallQuery bewusst NICHT,
// damit bestehende Verbraucher unberuehrt bleiben. Die Prom-Schicht referenziert
// ausschliesslich Heimdall.Abstractions — storage-agnostisch.
// ---------------------------------------------------------------------------

/// <summary>Matcher-Operator fuer Label-Selektion (PromQL =, !=, =~, !~).</summary>
public enum HMatchOp { Eq = 0, Ne = 1, Re = 2, Nre = 3 }

/// <summary>Ein Label-Matcher: Name, Wert, Operator.</summary>
public sealed record HLabelMatcher(string Name, string Value, HMatchOp Op);

/// <summary>
/// Ein geholter Metrik-Punkt mit bereits geparstem Label-Set. Labels sind ROHE
/// OTel-Keys (service.name, http.route, …) samt Resource-Attributen; das
/// Prom-Layer fuehrt service.name-&gt;job und Namens-Mapping durch. Histogramm-
/// Punkte tragen BucketCounts/ExplicitBounds; Counter/Gauge nur Value.
/// </summary>
public sealed record HMetricPointView(
    string Name,
    string? Unit,
    HMetricType Type,
    HTemporality Temporality,
    long TimeUnixNano,
    double Value,
    long? Count,
    double? Sum,
    double? Min,
    double? Max,
    IReadOnlyList<long>? BucketCounts,
    IReadOnlyList<double>? ExplicitBounds,
    IReadOnlyDictionary<string, string> Labels,
    string? ScopeName);

/// <summary>
/// Abfrage an den Metric-Source: eine Menge OTel-nativer Namen (vom Prom-Layer
/// per Namens-Mapping aufgeloest), optionale Label-Matcher (angewendet auf ROHE
/// Labels), ein Zeitfenster in Unix-Nanosekunden und ein Limit.
/// </summary>
public sealed record HMetricQuery(
    IReadOnlyList<string> Names,
    IReadOnlyList<HLabelMatcher>? Matchers,
    long? FromUnixNano,
    long? ToUnixNano,
    int Limit = 20000);

/// <summary>
/// Metric-Lese-Seite; pro Backend eine Implementierung (Walhalla, SQLite).
/// Spiegel zu <see cref="IHeimdallQuery"/>, aber auf die Beduerfnisse der
/// PromQL-Auswertung zugeschnitten: Metrik-/Label-Discovery plus Punkt-Fetch
/// mit Matcher-Filter.
/// </summary>
public interface IHeimdallMetricSource
{
    /// <summary>Alle distinkten Metrik-Namen (OTel-native) im Zeitfenster.</summary>
    IReadOnlyList<string> ListMetricNames(long? fromUnixNano = null, long? toUnixNano = null);

    /// <summary>Alle distinkten Label-Namen (roh, inkl. Resource-Attrs) im Zeitfenster.</summary>
    IReadOnlyList<string> ListLabelNames(IReadOnlyList<HLabelMatcher>? matchers = null,
                                         long? fromUnixNano = null, long? toUnixNano = null);

    /// <summary>Alle Werte eines Labels im Zeitfenster (optional matcher-gefiltert).</summary>
    IReadOnlyList<string> ListLabelValues(string labelName,
                                          IReadOnlyList<HLabelMatcher>? matchers = null,
                                          long? fromUnixNano = null, long? toUnixNano = null);

    /// <summary>Holt Metrik-Punkte gemaess <paramref name="query"/> (Name(n) + Matcher + Zeit).</summary>
    IReadOnlyList<HMetricPointView> FetchPoints(HMetricQuery query);
}