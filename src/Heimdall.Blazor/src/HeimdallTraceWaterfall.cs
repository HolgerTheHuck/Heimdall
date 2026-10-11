using System;
using System.Collections.Generic;
using System.Globalization;

namespace Heimdall.Blazor;

/// <summary>
/// Layout-Helfer für den Span-Zeitstrahl (Tabelle + Histogramm, rein SSR): Zeilen-
/// Reihenfolge aus der Parent-Chain (DFS-Preorder = Parent vor Child), Trace-Spanne
/// (Wall-Clock), Span-Start-Histogramm und %-Positionierung der Balken-Spalte.
/// Farbe je Service (stabiler Hash über <c>service.name</c>), sonst nach
/// <see cref="HSpanKind"/> (Server/Client/Internal/Producer/Consumer); Fehler-Spans
/// (<see cref="HStatusCode.Error"/>) overriden rot. Bewusst intern (via IVT für
/// Tests sichtbar) und wirft niemals (kaputte Spans legen die UI nicht).
/// </summary>
internal static class HeimdallTraceWaterfall
{
    /// <summary>
    /// DFS-Preorder mit Tiefe aus der Parent-Chain (Render-Reihenfolge der Span-Tabelle:
    /// Parent vor Child, Kinder nach Startzeit). Sicherheitsnetz für Zyklen/Verwaiste:
    /// nicht erreichte Spans hängen bei Tiefe 0 an. Wirft nie.
    /// </summary>
    public static IReadOnlyList<(Heimdall.SpanRow Span, int Depth)> Order(
        IReadOnlyList<Heimdall.SpanRow>? spans)
    {
        var ordered = new List<(Heimdall.SpanRow Span, int Depth)>();
        if (spans is null || spans.Count == 0) return ordered;

        var byId = new Dictionary<string, Heimdall.SpanRow>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<Heimdall.SpanRow>>(StringComparer.Ordinal);
        var roots = new List<Heimdall.SpanRow>();
        foreach (var s in spans)
        {
            byId[s.SpanId] = s;
            if (!string.IsNullOrEmpty(s.ParentSpanId))
            {
                if (!children.TryGetValue(s.ParentSpanId, out var list))
                {
                    list = new List<Heimdall.SpanRow>();
                    children[s.ParentSpanId] = list;
                }
                list.Add(s);
            }
        }
        // Roots: ohne Parent ODER dessen Parent nicht in dieser Trace.
        foreach (var s in spans)
        {
            if (string.IsNullOrEmpty(s.ParentSpanId) || !byId.ContainsKey(s.ParentSpanId))
                roots.Add(s);
        }
        roots.Sort((a, b) => a.StartUnixNano.CompareTo(b.StartUnixNano));
        foreach (var kv in children)
            kv.Value.Sort((a, b) => a.StartUnixNano.CompareTo(b.StartUnixNano));

        void Dfs(Heimdall.SpanRow node, int depth)
        {
            ordered.Add((node, depth));
            if (children.TryGetValue(node.SpanId, out var kids))
                foreach (var k in kids) Dfs(k, depth + 1);
        }
        foreach (var r in roots) Dfs(r, 0);
        // Sicherheitsnetz: falls einige Spans durch Zyklen/Verwaistes nicht erreicht wurden.
        if (ordered.Count < spans.Count)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (sp, _) in ordered) seen.Add(sp.SpanId);
            foreach (var s in spans)
                if (!seen.Contains(s.SpanId)) ordered.Add((s, 0));
        }
        return ordered;
    }

    /// <summary>
    /// Kritischer Pfad (Grafana-Parität): die Kette der Spans, die die Trace-Latenz
    /// dominieren — von jeder Wurzel aus wird rekursiv dem Kind mit dem größten End-
    /// Zeitpunkt gefolgt (das Kind, auf das der Parent „wartete"). Liefert die Menge
    /// der SpanIds auf diesem Pfad (Wurzeln inklusive). Wirft nie (null/leer → leer).
    /// </summary>
    public static IReadOnlySet<string> CriticalPath(IReadOnlyList<Heimdall.SpanRow>? spans)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (spans is null || spans.Count == 0) return result;

        var byId = new Dictionary<string, Heimdall.SpanRow>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<Heimdall.SpanRow>>(StringComparer.Ordinal);
        foreach (var s in spans)
        {
            byId[s.SpanId] = s;
            if (!string.IsNullOrEmpty(s.ParentSpanId))
            {
                if (!children.TryGetValue(s.ParentSpanId, out var list))
                {
                    list = new List<Heimdall.SpanRow>();
                    children[s.ParentSpanId] = list;
                }
                list.Add(s);
            }
        }

        void Walk(Heimdall.SpanRow node)
        {
            result.Add(node.SpanId);
            if (!children.TryGetValue(node.SpanId, out var kids) || kids.Count == 0) return;
            Heimdall.SpanRow best = kids[0];
            foreach (var k in kids) if (k.EndUnixNano > best.EndUnixNano) best = k;
            Walk(best);
        }

        // Wurzeln: ohne Parent ODER dessen Parent nicht in dieser Trace.
        foreach (var s in spans)
            if (string.IsNullOrEmpty(s.ParentSpanId) || !byId.ContainsKey(s.ParentSpanId))
                Walk(s);
        return result;
    }

    /// <summary>
    /// Trace-Spanne (Wall-Clock: min Start .. max End). Garantiert End &gt; Start
    /// (Fallback Start+1). Wirft nie (null/leer → (0,1)).
    /// </summary>
    public static (long StartUnixNano, long EndUnixNano) TraceRange(
        IReadOnlyList<Heimdall.SpanRow>? spans)
    {
        if (spans is null || spans.Count == 0) return (0, 1);
        long tStart = long.MaxValue, tEnd = long.MinValue;
        foreach (var s in spans)
        {
            if (s.StartUnixNano < tStart) tStart = s.StartUnixNano;
            if (s.EndUnixNano > tEnd) tEnd = s.EndUnixNano;
        }
        if (tEnd <= tStart) tEnd = tStart + 1;
        return (tStart, tEnd);
    }

    /// <summary>
    /// Span-Start-Histogramm: gleichmäßige Buckets über die Trace-Spanne, Buckets mit
    /// Count 0 bleiben enthalten (leere Säule). Wirft nie.
    /// </summary>
    public static IReadOnlyList<(int Count, long FromUnixNano, long ToUnixNano)> Histogram(
        IReadOnlyList<Heimdall.SpanRow>? spans,
        (long StartUnixNano, long EndUnixNano) range,
        int buckets = 20)
    {
        if (buckets < 1) buckets = 1;
        var result = new (int Count, long FromUnixNano, long ToUnixNano)[buckets];
        long span = range.EndUnixNano - range.StartUnixNano;
        if (span <= 0) span = 1;
        for (var i = 0; i < buckets; i++)
        {
            var from = range.StartUnixNano + span * i / buckets;
            var to = range.StartUnixNano + span * (i + 1) / buckets;
            if (to <= from) to = from + 1;
            result[i] = (0, from, to);
        }
        if (spans is null) return result;
        foreach (var s in spans)
        {
            var idx = (int)((s.StartUnixNano - range.StartUnixNano) * (double)buckets / span);
            if (idx < 0) idx = 0;
            if (idx >= buckets) idx = buckets - 1;
            result[idx].Count++;
        }
        return result;
    }

    /// <summary>
    /// Inline-Style des Balkens: "left:X%;width:Y%;background:COLOR" — Prozentwerte
    /// invariant formatiert (deutsches Komma würde CSS brechen), geclampt auf [0..100] %,
    /// Mindestbreite 0.5 % (plus CSS min-width:2px). Farbe via <see cref="ColorFor"/>
    /// (Fehler-Override). Wirft nie (rangeSpanNs ≤ 0 → Vollbreite).
    /// </summary>
    public static string BarStyle(Heimdall.SpanRow s,
        long rangeStartUnixNano, long rangeSpanNs, string? service = null)
    {
        double left = 0, width = 100;
        if (rangeSpanNs > 0 && s is not null)
        {
            left = (s.StartUnixNano - rangeStartUnixNano) / (double)rangeSpanNs * 100.0;
            width = (s.EndUnixNano - s.StartUnixNano) / (double)rangeSpanNs * 100.0;
            if (left < 0) left = 0;
            if (left > 100) left = 100;
            if (width < 0.5) width = 0.5;
            if (left + width > 100) width = 100 - left;
            if (width < 0.5) width = 0.5;
        }
        return "left:" + Pct(left) + "%;width:" + Pct(width) + "%;background:" + ColorFor(s, service);
    }

    /// <summary>Einrückung für die Tabelle: "margin-left:{depth*0.9}rem" (invariant),
    /// "" bei Tiefe 0. Wirft nie.</summary>
    public static string Indent(int depth)
    {
        if (depth <= 0) return string.Empty;
        return "margin-left:" + (depth * 0.9).ToString("0.#", CultureInfo.InvariantCulture) + "rem";
    }

    /// <summary>CSS-Farbe (Token/Literal) je Span — Service-Farbe zuerst (stabiler
    /// Hash über <c>service.name</c>), sonst Kind-Farben, Fehler-Override
    /// var(--hmd-err). null → var(--hmd-warn). Wirft nie.</summary>
    public static string ColorFor(Heimdall.SpanRow? s, string? service = null)
    {
        if (s is null) return "var(--hmd-warn)";
        if (s.StatusCode == (int)Heimdall.HStatusCode.Error) return "var(--hmd-err)";
        if (!string.IsNullOrEmpty(service)) return ServiceColor(service);
        return ((Heimdall.HSpanKind)s.Kind) switch
        {
            Heimdall.HSpanKind.Server => "var(--hmd-accent)",
            Heimdall.HSpanKind.Client => "var(--hmd-ok)",
            Heimdall.HSpanKind.Producer => "#a371f7",
            Heimdall.HSpanKind.Consumer => "#a371f7",
            _ => "var(--hmd-warn)",   // Internal / Unspecified
        };
    }

    /// <summary>Service-Name aus den Resource-Attributen (Fallback Span-Attribute):
    /// <c>service.name</c> (OTel-Punkt-Form) oder <c>service_name</c> (Loki-Unterstrich-
    /// Form). Leer, wenn keiner gesetzt oder das JSON unparsebar ist. Wirft nie.</summary>
    public static string ServiceFor(Heimdall.SpanRow? s)
    {
        if (s is null) return string.Empty;
        foreach (var a in HeimdallCharting.ParseAttrs(s.ResourceJson))
            if (a.Key == "service.name" || a.Key == "service_name") return a.Value;
        foreach (var a in HeimdallCharting.ParseAttrs(s.AttrsJson))
            if (a.Key == "service.name" || a.Key == "service_name") return a.Value;
        return string.Empty;
    }

    /// <summary>Stabile Farbe pro Service-Name (FNV-1a über eine feste Palette), damit
    /// derselbe Service tracen-/seitenübergreifend gleich gefärbt ist. Leerer Service →
    /// var(--hmd-warn). Wirft nie.</summary>
    public static string ServiceColor(string? service)
    {
        if (string.IsNullOrEmpty(service)) return "var(--hmd-warn)";
        uint h = 2166136261;
        foreach (var c in service) { h ^= c; h *= 16777619; }
        return ServicePalette[(int)(h % (uint)ServicePalette.Length)];
    }

    /// <summary>Ob der Balken breit genug für ein Dauer-Label im Balken ist
    /// (Schwellwert 10 % der Trace-Spanne — schmale Balken zeigen nur den Balken,
    /// die Dauer steht weiterhin in der Dauer-Spalte). Wirft nie.</summary>
    public static bool ShowDurationLabel(Heimdall.SpanRow? s,
        long rangeStartUnixNano, long rangeSpanNs)
    {
        if (s is null || rangeSpanNs <= 0) return false;
        var width = (s.EndUnixNano - s.StartUnixNano) / (double)rangeSpanNs * 100.0;
        return width >= 10.0;
    }

    /// <summary>Feste Service-Palette (kein Rot — Rot ist dem Fehler-Override
    /// vorbehalten); 12 optisch unterscheidbare Hues für die Service-Legende.</summary>
    private static readonly string[] ServicePalette =
    {
        "var(--hmd-accent)", "var(--hmd-ok)", "var(--hmd-warn)", "#a371f7", "#79c0ff",
        "#56d364", "#f0883e", "#d2a8ff", "#39c5cf", "#ff8a65", "#82aaff", "#c792ea",
    };

    /// <summary>Prozentzahl invariant ("12.5", nie "12,5"). Wirft nie.</summary>
    public static string Pct(double p) => p.ToString("0.##", CultureInfo.InvariantCulture);
}