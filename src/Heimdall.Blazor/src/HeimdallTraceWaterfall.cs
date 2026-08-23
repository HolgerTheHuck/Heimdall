using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Heimdall.Blazor;

/// <summary>
/// Server-seitiger SVG-Bau eines Trace-Wasserfalls (Gantt/Flame): jede Span wird als
/// horizontaler Balken positioniert nach Start/Dauer relativ zur Trace-Spanne, eingerückt
/// nach Tiefe aus der Parent-Chain (DFS-Preorder = Render-Reihenfolge, Parent vor Child).
/// Farbe nach <see cref="HSpanKind"/> (Server/Client/Internal/Producer/Consumer), Fehler-
/// Spans (<see cref="HStatusCode.Error"/>) override rot. Balken tragen <c>data-*</c>-Attribute,
/// sodass das bestehende Hover-Tooltip (<c>heimdall.js</c>) greift — kein extra JS. Bewusst
/// intern (via IVT für Tests sichtbar) und wirft niemals (kaputte Spans legen die UI nicht).
/// </summary>
internal static class HeimdallTraceWaterfall
{
    /// <summary>
    /// Erzeugt das Wasserfall-SVG für die gegebenen Spans. Liefert einen leeren String bei
    /// ≤1 Span (Aufrufer zeigt stattdessen einen Hinweis). <paramref name="width"/> ist die
    /// viewBox-Breite; das SVG skaliert via <c>width:100%</c>.
    /// </summary>
    public static string RenderWaterfallSvg(IReadOnlyList<Heimdall.SpanRow> spans, int width = 1000)
    {
        if (spans is null || spans.Count <= 1) return string.Empty;

        const double padLeft = 230;   // Label-Bereich (Span-Name)
        const double padRight = 16;
        const double padTop = 26;     // Zeitachse
        const double padBottom = 6;
        const double rowH = 24;
        const double barH = 16;

        double plotW = width - padLeft - padRight;
        if (plotW < 80) plotW = 80;

        // Trace-Spanne (Wall-Clock).
        long tStart = long.MaxValue, tEnd = long.MinValue;
        foreach (var s in spans)
        {
            if (s.StartUnixNano < tStart) tStart = s.StartUnixNano;
            if (s.EndUnixNano > tEnd) tEnd = s.EndUnixNano;
        }
        if (tEnd <= tStart) tEnd = tStart + 1;
        double span = tEnd - tStart;

        // DFS-Preorder mit Tiefe aus der Parent-Chain.
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

        // Preorder-Traversal → geordnete Liste mit Tiefe.
        var ordered = new List<(Heimdall.SpanRow Span, int Depth)>(spans.Count);
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

        double height = padTop + ordered.Count * rowH + padBottom;
        var sb = new StringBuilder(ordered.Count * 128);
        sb.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(F(height))
          .Append("\" class=\"hmd-chart hmd-waterfall\" role=\"img\" aria-label=\"Trace-Wasserfall\" preserveAspectRatio=\"xMidYMid meet\">");

        // Zeitachse (5 relative Ticks).
        for (int i = 0; i <= 4; i++)
        {
            double x = padLeft + plotW * i / 4.0;
            long off = (long)(span * i / 4.0);
            sb.Append("<line class=\"hmd-chart-grid\" x1=\"").Append(F(x)).Append("\" y1=\"")
              .Append(F(padTop)).Append("\" x2=\"").Append(F(x)).Append("\" y2=\"")
              .Append(F(height - padBottom)).Append("\"/>");
            sb.Append("<text class=\"hmd-chart-label hmd-chart-xlabel\" x=\"").Append(F(x))
              .Append("\" y=\"").Append(F(padTop - 8)).Append("\" text-anchor=\"")
              .Append(i == 0 ? "start" : i == 4 ? "end" : "middle").Append("\">+")
              .Append(Esc(HeimdallFmt.Dur(off))).Append("</text>");
        }

        // Balken je Span (DFS-Reihenfolge).
        for (int i = 0; i < ordered.Count; i++)
        {
            var (s, depth) = ordered[i];
            double y = padTop + i * rowH;
            double barY = y + (rowH - barH) / 2.0;
            double bx = padLeft + (s.StartUnixNano - tStart) / span * plotW;
            double bw = Math.Max(2, (s.EndUnixNano - s.StartUnixNano) / span * plotW);
            if (s.EndUnixNano <= s.StartUnixNano) bw = 2;
            string color = ColorFor(s);
            double labelIndent = 6 + depth * 16;

            // Name links (eingerückt).
            sb.Append("<text class=\"hmd-chart-label hmd-waterfall-name\" x=\"").Append(F(labelIndent))
              .Append("\" y=\"").Append(F(barY + barH * 0.75)).Append("\" text-anchor=\"start\">")
              .Append(Esc(Trunc(s.Name, 26))).Append("</text>");

            // Balken (mit data-* für Hover-Tooltip; data-v = DurationNs → JS fmtDur).
            sb.Append("<rect class=\"hmd-chart-pt hmd-waterfall-bar\" x=\"").Append(F(bx))
              .Append("\" y=\"").Append(F(barY)).Append("\" width=\"").Append(F(bw))
              .Append("\" height=\"").Append(F(barH)).Append("\" rx=\"2\" fill=\"").Append(color)
              .Append("\" data-t=\"").Append(s.StartUnixNano.ToString(CultureInfo.InvariantCulture))
              .Append("\" data-v=\"").Append(s.DurationNs.ToString(CultureInfo.InvariantCulture))
              .Append("\" data-label=\"").Append(Esc(s.Name)).Append("\"/>");

            // Dauer-Label im/rechts am Balken, wenn breit genug.
            if (bw > 34)
            {
                sb.Append("<text class=\"hmd-chart-label hmd-waterfall-dur\" x=\"").Append(F(bx + 3))
                  .Append("\" y=\"").Append(F(barY + barH * 0.75)).Append("\" text-anchor=\"start\">")
                  .Append(Esc(HeimdallFmt.Dur(s.DurationNs))).Append("</text>");
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string ColorFor(Heimdall.SpanRow s)
    {
        if (s.StatusCode == (int)Heimdall.HStatusCode.Error) return "var(--hmd-err)";
        return ((Heimdall.HSpanKind)s.Kind) switch
        {
            Heimdall.HSpanKind.Server => "var(--hmd-accent)",
            Heimdall.HSpanKind.Client => "var(--hmd-ok)",
            Heimdall.HSpanKind.Producer => "#a371f7",
            Heimdall.HSpanKind.Consumer => "#a371f7",
            _ => "var(--hmd-warn)",   // Internal / Unspecified
        };
    }

    private static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}