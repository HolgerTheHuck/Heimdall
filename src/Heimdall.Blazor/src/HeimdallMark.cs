using Microsoft.AspNetCore.Components;

namespace Heimdall.Blazor;

// ---------------------------------------------------------------------------
// Inline-SVG-Glyphen der „Nachtwacht“-Identität — ein Asset, keine Requests,
// kein JS. Die Heimdall-Marke ist das Zielfernrohr (⌖-Gedanke) mit Messing-
// Optik, Nebel-Ticks und dem Bifröst-Bogen durch das Zentrum; die Navcard-
// Glyphen sind schlichte Strichzeichnungen (stroke=currentColor — Färbung
// übernimmt CSS, im Navcard-Kontext Messing). CSS-Variablen funktionieren in
// SVG-Präsentationsattributen NICHT (var() wird dort nicht aufgeloest), daher
// style="…" statt stroke="var(…)".
// ---------------------------------------------------------------------------

internal static class HeimdallMark
{
    /// <summary>Die Heimdall-Marke (24×24). <paramref name="gid"/> ist die Id
    /// des Gradienten-Defs — PRO INSTANZ EINDEUTIG vergeben (Nav und Footer
    /// teilen sich sonst eine doppelte Id im selben Dokument).</summary>
    public static MarkupString Brand(string gid)
    {
        var svg = $"""
            <svg viewBox="0 0 24 24" aria-hidden="true" xmlns="http://www.w3.org/2000/svg" fill="none">
            <defs><linearGradient id="{gid}" x1="4.5" y1="12" x2="19.5" y2="12" gradientUnits="userSpaceOnUse">
            <stop offset="0" style="stop-color:var(--hmd-bifrost-1)"/><stop offset=".34" style="stop-color:var(--hmd-bifrost-2)"/><stop offset=".67" style="stop-color:var(--hmd-bifrost-3)"/><stop offset="1" style="stop-color:var(--hmd-bifrost-4)"/>
            </linearGradient></defs>
            <path d="M4.5 12a7.5 7.5 0 0 1 15 0" style="stroke:url(#{gid})" stroke-width="2.4" stroke-linecap="round"/>
            <circle cx="12" cy="12" r="7.5" style="stroke:var(--hmd-accent)" stroke-width="1.8"/>
            <circle cx="12" cy="12" r="1.7" style="fill:var(--hmd-accent)"/>
            <path d="M12 1.5v3.2M12 19.3v3.2M1.5 12h3.2M19.3 12h3.2" style="stroke:var(--hmd-dim)" stroke-width="1.5" stroke-linecap="round"/>
            </svg>
            """;
        return new MarkupString(svg);
    }

    /// <summary>Navcard-Strichglyphe (24×24, stroke=currentColor) nach Schlüssel:
    /// dashboard/traces/logs/metrics/endpoints/dashboards.</summary>
    public static MarkupString Navcard(string key)
    {
        var body = key switch
        {
            // Balkendiagramm (Monitor-Dashboard)
            "dashboard" => """<path d="M5 20v-7M12 20V5M19 20v-10"/>""",
            // Mini-Wasserfall (Spans über Zeit)
            "traces" => """<path d="M4 6h6M9 12h9M6 18h12"/>""",
            // Dokument mit Zeilen (Log)
            "logs" => """<path d="M7 3h7l4 4v14H7z"/><path d="M10 11h5M10 15h5M10 19h3"/>""",
            // Trendlinie mit Endpunkt (Metrik)
            "metrics" => """<path d="M4 19L9 13l4 3 7-9"/><circle cx="20" cy="7" r="1.3" fill="currentColor" stroke="none"/>""",
            // Route zwischen zwei Punkten (Endpunkte)
            "endpoints" => """<circle cx="5" cy="19" r="1.8"/><circle cx="19" cy="5" r="1.8"/><path d="M7 17c5-2 5-7 10-10"/>""",
            // Kachel-Raster (Dashboards)
            "dashboards" => """<rect x="4" y="4" width="7" height="7" rx="1"/><rect x="13" y="4" width="7" height="7" rx="1"/><rect x="4" y="13" width="7" height="7" rx="1"/><rect x="13" y="13" width="7" height="7" rx="1"/>""",
            _ => "",
        };
        var svg = $"""
            <svg viewBox="0 0 24 24" aria-hidden="true" xmlns="http://www.w3.org/2000/svg" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">{body}</svg>
            """;
        return new MarkupString(svg);
    }
}