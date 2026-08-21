using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// OTel <-> Prometheus Namens-Mapping.
//   Counter  (HMetricType.Sum) -> base + "_total"
//   Histogram                 -> base + "_bucket" / "_sum" / "_count"
//   Gauge                     -> base
// wobei base = otelName ('.'->'_') + UnitSuffix (s/ms->"_seconds", By->"_bytes",
// %->"_ratio"). ms-Werte werden /1000 skaliert (s. SeriesResolver).
// service.name -> job UND service_name (beide exponiert, s. MapLabelKeys);
// sonstige Label-Keys '.' -> '_'. job ist das klassische Prom-Scrape-Label
// (heimdall-overview nutzt es), service_name die OTel-Collector-Konvention
// (Community-Dashboards wie otel-dotnet-webapi filtern danach). Beide werden
// je Serie gesetzt, damit Dashboards beider Konventionen ohne Anpassung laufen.
//
// Beide Aliase queryable: Counter/Gauge werden zusaetzlich unter dem rohen
// OTel-Namen ( '.'->'_' ) exponiert. Histogramme nur als Prom-Familie (das ist
// das, was Community-Dashboards per histogram_quantile erwarten).
// ---------------------------------------------------------------------------

/// <summary>
/// OTel- zu Prometheus-Namens-Mapping (Counter→<c>_total</c>, Histogramm→
/// <c>_bucket/_sum/_count</c>, Gauge→Name) inkl. Unit-Suffix (<c>s/ms</c>→<c>_seconds</c>,
/// <c>By</c>→<c>_bytes</c>, <c>%</c>→<c>_ratio</c>) und Reverse-Lookup fuer die Discovery.
/// Siehe Dateikommentar.
/// </summary>
public sealed class MetricNameMapper
{
    // --- Legacy-Aliase: .NET-Runtime-Metriken -----------------------------
    // Der .NET 9+ built-in `System.Runtime`-Meter (den OpenTelemetry.
    // Instrumentation.Runtime ab 1.10 auf .NET 9+ nutzt) emittiert `dotnet.*`-
    // Namen gemäß den aktuellen OTel Semantic Conventions. Community-Dashboards
    // wie otel-dotnet-webapi (gnetId 20568) fragen aber die ÄLTEREN
    // `process.*`-/`process.runtime.dotnet.*`-Namen ab, die der Runtime-
    // Instrumentation-Paket auf .NET 8 bzw. <1.10 erzeugt hat. Diese Tabelle
    // überbrückt die Lücke ADDITIV: jede `dotnet.*`-Runtime-Metrik wird
    // ZUSÄTZLICH unter ihren Legacy-Prom-Namen exponiert, sodass importierte
    // Dashboards ohne Anpassung laufen. Bekanntes Kompatibilitätsproblem, s.
    // petabridge/dotnet-grafana-dashboards#12. Die regulären Prom-Namen
    // (`dotnet_*`) bleiben erhalten — nichts wird entfernt.
    //
    // Mapping ist semantisch (nicht rein mechanisch), weil die Umbenennung
    // über den Prefix hinaus ging (`.count`-Suffixe entfallen, Größen-Namen
    // geändert). Prozess-Threads (`process_threads`) hat kein Gegenstück im
    // built-in Meter → bewusst nicht aliasiert (Panel bleibt leer).
    private static readonly IReadOnlyDictionary<string, string[]> RuntimeLegacyAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["dotnet.process.cpu.time"] =
                new[] { "process_cpu_time_seconds_total" },
            ["dotnet.process.memory.working_set"] =
                new[] { "process_memory_usage_bytes" },
            ["dotnet.assembly.count"] =
                new[] { "process_runtime_dotnet_assemblies_count" },
            ["dotnet.exceptions"] =
                new[] { "process_runtime_dotnet_exceptions_count_total" },
            ["dotnet.gc.heap.total_allocated"] =
                new[] { "process_runtime_dotnet_gc_allocations_size" },
            ["dotnet.gc.collections"] =
                new[] { "process_runtime_dotnet_gc_collections_count_total" },
            ["dotnet.gc.last_collection.memory.committed_size"] =
                new[] { "process_runtime_dotnet_gc_committed_memory_size_bytes" },
            ["dotnet.gc.last_collection.heap.fragmentation.size"] =
                new[] { "process_runtime_dotnet_gc_heap_fragmentation_size_bytes" },
            ["dotnet.gc.last_collection.heap.size"] =
                new[] { "process_runtime_dotnet_gc_heap_size_bytes",
                        "process_runtime_dotnet_gc_objects_size" },
            ["dotnet.thread_pool.queue.length"] =
                new[] { "process_runtime_dotnet_thread_pool_queue_length" },
            ["dotnet.thread_pool.thread.count"] =
                new[] { "process_runtime_dotnet_thread_pool_threads_count" },
        };

    // Reverse-Map (Legacy-Prom-Name -> OTel-Name) für ResolvePromToOtel.
    private static readonly IReadOnlyDictionary<string, string> RuntimeLegacyReverse =
        BuildRuntimeLegacyReverse();

    private static Dictionary<string, string> BuildRuntimeLegacyReverse()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in RuntimeLegacyAliases)
            foreach (var alias in kv.Value)
                d[alias] = kv.Key;
        return d;
    }

    /// <summary>
    /// Ergänzende Legacy-Prom-Namen (Community-Dashboard-Kompatibilität) für
    /// einen OTel-Metriknamen — z. B. <c>dotnet.thread_pool.thread.count</c>
    /// zusätzlich als <c>process_runtime_dotnet_thread_pool_threads_count</c>.
    /// Leer, wenn kein Alias definiert. Additiv: die regulären Prom-Namen
    /// bleiben exponiert.
    /// </summary>
    public IReadOnlyList<string> LegacyAliases(string otelName)
        => RuntimeLegacyAliases.TryGetValue(otelName, out var a) ? a : Array.Empty<string>();

    /// <summary>Prom-Basisname: OTel-Name mit '.'->'_' plus Unit-Suffix.</summary>
    public string PromBase(string otelName, string? unit)
        => otelName.Replace('.', '_') + UnitSuffix(unit);

    /// <summary>Unit-Suffix nach Prom-Konvention.</summary>
    public static string UnitSuffix(string? unit) => unit switch
    {
        "s" => "_seconds",
        "ms" => "_seconds",
        "By" => "_bytes",
        "%" => "_ratio",
        _ => string.Empty
    };

    /// <summary>Skaliert einen Wert passend zur Unit (ms -> s: /1000).</summary>
    public static double ScaleValue(string? unit, double v) => unit == "ms" ? v / 1000.0 : v;

    /// <summary>Skaliert Histogram-Bounds (ms -> s).</summary>
    public static double ScaleBound(string? unit, double b) => unit == "ms" ? b / 1000.0 : b;

    /// <summary>Prom-Name(n), die ein OTel-Punkt als Counter exponiert (Prom-konform + roher Alias + Legacy-Aliase).</summary>
    public IReadOnlyList<string> CounterNames(string otelName, string? unit)
    {
        var prom = PromBase(otelName, unit) + "_total";
        var raw = otelName.Replace('.', '_');
        var legacy = LegacyAliases(otelName);
        if (legacy.Count == 0)
            return StringComparer.Ordinal.Equals(prom, raw) ? new[] { prom } : new[] { prom, raw };
        var list = new List<string>(2 + legacy.Count) { prom };
        if (!StringComparer.Ordinal.Equals(prom, raw)) list.Add(raw);
        foreach (var a in legacy) if (!list.Contains(a, StringComparer.Ordinal)) list.Add(a);
        return list;
    }

    /// <summary>Prom-Name fuer einen Gauge (Prom-konform + roher Alias + Legacy-Aliase).</summary>
    public IReadOnlyList<string> GaugeNames(string otelName, string? unit)
    {
        var prom = PromBase(otelName, unit);
        var raw = otelName.Replace('.', '_');
        var legacy = LegacyAliases(otelName);
        if (legacy.Count == 0)
            return StringComparer.Ordinal.Equals(prom, raw) ? new[] { prom } : new[] { prom, raw };
        var list = new List<string>(2 + legacy.Count) { prom };
        if (!StringComparer.Ordinal.Equals(prom, raw)) list.Add(raw);
        foreach (var a in legacy) if (!list.Contains(a, StringComparer.Ordinal)) list.Add(a);
        return list;
    }

    /// <summary>Prom-Histogramm-Familie: _bucket / _sum / _count (Basis inkl. Unit-Suffix).</summary>
    public (string bucket, string sum, string count) HistogramNames(string otelName, string? unit)
    {
        var b = PromBase(otelName, unit);
        return (b + "_bucket", b + "_sum", b + "_count");
    }

    /// <summary>
    /// Löst einen Prom-Metriknamen aus einer Abfrage auf 1..N OTel-Namen auf.
    /// "_total"/"_bucket"/"_sum"/"_count" werden abgeschnitten; Unit-Suffix bleibt
    /// unberücksichtigt (OTel speichert den Originalnamen), d. h. wir suchen per
    /// Suffix-Strip + rohem Treffer. Trägt der Prom-Name kein Suffix, ist es evtl.
    /// ein roher OTel-Alias -> direkt zurück.
    /// </summary>
    public IReadOnlyList<string> ResolvePromToOtel(string promName, IEnumerable<string> knownOtelNames)
    {
        // known einmal materialisieren (wird unten mehrfach enumeriert).
        var known = knownOtelNames as IReadOnlyList<string> ?? knownOtelNames.ToList();

        // Legacy-Alias der .NET-Runtime-Metriken: dotnet.* <-> process.*/
        // process_runtime_dotnet_*. Wenn der abgefragte Prom-Name ein bekannter
        // Legacy-Name ist UND der zugehörige OTel-Name tatsächlich gespeichert
        // ist, direkt darauf auflösen (der mechanische Suffix-Strip unten findet
        // sonst kein Mapping, da die Umbenennung über den Prefix hinaus ging).
        if (RuntimeLegacyReverse.TryGetValue(promName, out var legacy) && ContainsOtel(known, legacy))
            return new[] { legacy };

        // Kandidaten: Prom-Name, um Prom-Suffix (_total/_bucket/_sum/_count) bereinigt,
        // und zusätzlich um Unit-Suffix (_seconds/_bytes/_ratio) bereinigt (in beiden
        // Reihenfolgen, da z. B. „..._seconds_bucket" erst _bucket, dann _seconds strippt).
        var candidates = new List<string> { promName, StripPromSuffix(promName) };
        foreach (var c in new[] { promName, StripPromSuffix(promName) })
            foreach (var u in new[] { "_seconds", "_bytes", "_ratio" })
                if (c.EndsWith(u, StringComparison.Ordinal) && c.Length > u.Length)
                    candidates.Add(c.Substring(0, c.Length - u.Length));

        foreach (var cand in candidates)
            foreach (var otel in known)
                if (otel.Replace('.', '_') == cand) return new[] { otel };
        return Array.Empty<string>();
    }

    private static bool ContainsOtel(IReadOnlyList<string> known, string otel)
    {
        for (int i = 0; i < known.Count; i++)
            if (StringComparer.Ordinal.Equals(known[i], otel)) return true;
        return false;
    }

    private static string StripPromSuffix(string n)
    {
        foreach (var s in new[] { "_bucket", "_count", "_sum", "_total" })
            if (n.EndsWith(s, StringComparison.Ordinal)) return n.Substring(0, n.Length - s.Length);
        return n;
    }

    /// <summary>
    /// Prom-Label-Namen, unter denen ein OTel-Label-Key exponiert wird.
    /// <c>service.name</c> wird doppelt exponiert — als <c>job</c> (klassisches
    /// Prom-Scrape-Label, z. B. heimdall-overview) und als <c>service_name</c>
    /// (OTel-Collector-Konvention, z. B. otel-dotnet-webapi) —, sodass Dashboards
    /// beider Konventionen filtern können. Sonstige Keys: '.' -> '_'.
    /// </summary>
    public static IReadOnlyList<string> MapLabelKeys(string otelKey)
        => otelKey == "service.name"
            ? new[] { "job", "service_name" }
            : new[] { otelKey.Replace('.', '_') };

    /// <summary>Primärer Prom-Label-Name (erstes Element von <see cref="MapLabelKeys"/>):
    /// <c>service.name</c> -> <c>job</c>, sonst '.' -> '_'.</summary>
    public static string MapLabelKey(string otelKey) => MapLabelKeys(otelKey)[0];
}