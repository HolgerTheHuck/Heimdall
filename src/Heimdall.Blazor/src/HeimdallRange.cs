using System;
using System.Collections.Generic;
using System.Linq;

namespace Heimdall.Blazor;

/// <summary>
/// Reine, werferfreie Auflösung des konfigurierbaren Zeitbereichs in Unix-Nanosekunden-
/// Schranken. Verbindet die UI-Presets (15m/1h/24h/7d/Alles) und optionale explizite
/// from/to mit den Query-Verträgen (<see cref="Heimdall.TraceFilter"/>/
/// <see cref="Heimdall.LogSearch"/>/<see cref="Heimdall.IHeimdallQuery.MetricSeries"/>
/// haben alle FromUnixNano/ToUnixNano). Bewusst intern (via IVT fuer Tests sichtbar);
/// <see cref="NowUnixNano"/> ist die einzige nicht-deterministische Stelle und wird
/// daher fuer Tests als Parameter durchgereicht.
///
/// Konvention: explizite <c>from</c>/<c>to</c> überschreiben das Preset; <c>"all"</c>
/// oder fehlendes Preset + fehlende from/to → beide null (= unbegrenzt). Die Seiten
/// geben einen Default-Preset vor, sodass ein leeres Formular trotzdem ein Fenster
/// liefert.
/// </summary>
internal static class HeimdallRange
{
    public const long NanosPerSecond = 1_000_000_000L;
    public const long NanosPerMinute  = 60L * NanosPerSecond;
    public const long NanosPerHour    = 60L * NanosPerMinute;
    public const long NanosPerDay     = 24L * NanosPerHour;

    /// <summary>Ein Preset-Eintrag für das &lt;select&gt;; SpanNanos null = „Alles".</summary>
    public sealed record Preset(string Key, string Label, long? SpanNanos);

    /// <summary>Die Presets in UI-Reihenfolge (Key = Query-Wert).</summary>
    public static readonly IReadOnlyList<Preset> Presets = new[]
    {
        new Preset("15m", "15 Minuten",  15L * NanosPerMinute),
        new Preset("1h",  "1 Stunde",    NanosPerHour),
        new Preset("24h", "24 Stunden",  NanosPerDay),
        new Preset("7d",  "7 Tage",      7L * NanosPerDay),
        new Preset("all", "Alles",       null),
    };

    /// <summary>Aktueller Zeitpunkt als Unix-Nanosekunden (einzige Wanduhr-Quelle).</summary>
    public static long NowUnixNano() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() * NanosPerSecond;

    /// <summary>
    /// Löst Preset + explizite from/to zu einem Fenster auf. Explizite from/to
    /// überschreiben das Preset; fehlt beides, gilt <paramref name="fallbackPreset"/>
    /// (Default <c>"1h"</c>). <c>"all"</c> → beide null (unbegrenzt).
    /// </summary>
    public static TimeRange Resolve(string? preset, long? from, long? to, long nowUnixNano, string fallbackPreset = "1h")
    {
        if (from is not null || to is not null)
            return new TimeRange(from, to);

        var key = string.IsNullOrWhiteSpace(preset) ? fallbackPreset : preset;
        var p = Presets.FirstOrDefault(x => x.Key == key) ?? Presets[1];   // Fallback: 1h
        if (p.SpanNanos is null) return new TimeRange(null, null);          // "all"
        return new TimeRange(nowUnixNano - p.SpanNanos.Value, nowUnixNano);
    }

    /// <summary>Halteschranken (beide null = unbegrenzt).</summary>
    public sealed record TimeRange(long? From, long? To);
}