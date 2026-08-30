using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Heimdall.Blazor;

/// <summary>
/// Eine farbige Datenreihe für das Liniendiagramm. Top-Level-public, weil sie als
/// <c>[Parameter]</c>-Typ der public-Komponente <see cref="HeimdallChart"/> dient.
/// </summary>
public sealed record ChartSeries(string Label, string Color, IReadOnlyList<(long T, double V)> Points);

/// <summary>Eine Zeile eines Bargauge-Panels (Label, Wert, Max, Farbe, Einheit).</summary>
public sealed record BarGaugeRow(string Label, double Value, double Max, string Color, string? Unit);

/// <summary>Eine Scheibe eines Pie-Panels (Label, Wert, Farbe).</summary>
public sealed record PieSlice(string Label, double Value, string Color);

/// <summary>Eine Zeile eines Heatmap-Panels: Obergrenze des Histogramm-Buckets
/// (<c>+Inf</c> = <see cref="double.PositiveInfinity"/>), Anzeige-Label und die
/// inkrementellen Raten je Zeitspalte (aufsteigend nach <see cref="UpperBound"/>).</summary>
public sealed record HeatmapBucket(double UpperBound, string Label, IReadOnlyList<double> Values);

/// <summary>
/// Reine, Razor-freie Helfer fuer das Dashboard-Rendering: JSON-Attribut-Parser
/// (beide Backends schreiben identisches flaches <c>{"key":value}</c>), Histogramm-
/// Bucket-Parser, die server-seitige SVG-Koordinatenmathematik sowie die SVG-String-
/// Erzeugung (server-gerendert, kein JS). Bewusst intern und werfen niemals —
/// kaputtes JSON darf die UI nicht legen.
/// </summary>
internal static class HeimdallCharting
{
    // Palette: soweit moeglich CSS-Vars des Dark-Theme, Rest hex passend dazu.
    public static readonly string[] Palette =
    {
        "var(--hmd-accent)",
        "var(--hmd-ok)",
        "var(--hmd-warn)",
        "var(--hmd-err)",
        "#a371f7",
        "#79c0ff",
    };

    public static string ColorAt(int i) => Palette[i % Palette.Length];

    // ---------------------------------------------------------------------
    // DTOs (intern — nur innerhalb der Assembly + via IVT fuer Tests)
    // ---------------------------------------------------------------------

    public sealed record AttrKv(string Key, string Value);
    /// <summary>
    /// Eine skalierte Serie. <see cref="Points"/> ist die SVG-Koordinatenliste
    /// (<c>"x,y x,y …"</c>); <see cref="RawPoints"/> optional die urspruenglichen
    /// Datenpunkte (T in ns, V) fuer Progressive-Enhancement-Tooltips (JS, data-*-Attribute).
    /// null = alt: Punkte werden aus <see cref="Points"/> gesplittet (backward-compatible).
    /// </summary>
    public sealed record SeriesPath(string Label, string Color, string Points, IReadOnlyList<(long T, double V)>? RawPoints = null);
    public sealed record GridLine(double Y, string Label);
    public sealed record TickLabel(double X, string Label);

    // ---------------------------------------------------------------------
    // JSON-Parser
    // ---------------------------------------------------------------------

    /// <summary>Flaches Attribut-JSON <c>{"k":v,...}</c> → Liste von (Key, Value).</summary>
    public static IReadOnlyList<AttrKv> ParseAttrs(string? json)
    {
        var result = new List<AttrKv>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in root.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null) continue;
                result.Add(new AttrKv(p.Name, FormatToken(p.Value)));
            }
        }
        catch { /* malformed JSON -> leere Liste, UI laeuft weiter */ }
        return result;
    }

    public static IReadOnlyList<long> ParseLongs(string? json)
    {
        var result = new List<long>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.TryGetInt64(out var v)) result.Add(v);
        }
        catch { }
        return result;
    }

    public static IReadOnlyList<double> ParseDoubles(string? json)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.TryGetDouble(out var v)) result.Add(v);
        }
        catch { }
        return result;
    }

    private static string FormatToken(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString() ?? string.Empty;
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var l)) return l.ToString(CultureInfo.InvariantCulture);
                if (e.TryGetDouble(out var d)) return d.ToString("0.##", CultureInfo.InvariantCulture);
                return e.GetRawText();
            default: return e.GetRawText();
        }
    }

    // ---------------------------------------------------------------------
    // SVG-Koordinatenmathematik
    // ---------------------------------------------------------------------

    /// <summary>
    /// Berechnet aus einer oder mehreren Serien die fertigen SVG-Pfade, y-Gridlinien
    /// und x-Ticks. Entartete Eingaben (einzelner Punkt, konstanter Wert) bekommen
    /// kuenstlichen Headroom, sodass nie durch 0 geteilt wird und keine NaN/Infinity
    /// entstehen. Liefert null bei komplett leeren Serien (Aufrufer zeigt Platzhalter).
    /// </summary>
    public static ChartGeometry? ScaleChart(IReadOnlyList<ChartSeries> series, int width, int height)
    {
        if (series is null || series.Count == 0) return null;

        const double padLeft = 52, padRight = 14, padTop = 14, padBottom = 30;
        double plotW = width - padLeft - padRight;
        double plotH = height - padTop - padBottom;
        if (plotW <= 0 || plotH <= 0) return null;

        long xMin = long.MaxValue, xMax = long.MinValue;
        double yMin = double.MaxValue, yMax = double.MinValue;
        bool any = false;
        foreach (var s in series)
        {
            if (s.Points is null) continue;
            foreach (var (t, v) in s.Points)
            {
                // PromQL kann +Inf/NaN-Proben liefern (z. B. Division durch 0, rate über
                // leeren Fenstern, histogram_quantile ohne Buckets). Diese würden yMin/yMax
                // korruptieren (∞ als Max → alle endlichen Punkte kollabieren) und beim
                // JSON-Payload (AppendChartData) zum Serializer-Crash führen. Stattdessen
                // überspringen → Skalen nur über endliche Werte, Lücke statt Punkt.
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                any = true;
                if (t < xMin) xMin = t;
                if (t > xMax) xMax = t;
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }
        }
        if (!any) return null;

        // Headroom gegen Div-by-zero bei Ein-Punkt- / konstanten Serien.
        if (xMax <= xMin) xMax = xMin + 1;
        if (yMax <= yMin) yMax = yMin + 1.0;
        double pad = (yMax - yMin) * 0.1;
        double yLo = yMin - pad, yHi = yMax + pad;

        double xRange = xMax - xMin;
        double yRange = yHi - yLo;

        double X(long t) => padLeft + (t - xMin) / xRange * plotW;
        double Y(double v) => padTop + (1.0 - (v - yLo) / yRange) * plotH;

        var paths = new List<SeriesPath>(series.Count);
        int totalPoints = 0;
        foreach (var s in series)
        {
            if (s.Points is null || s.Points.Count == 0) continue;
            var sb = new StringBuilder(s.Points.Count * 12);
            // RawPoints gefiltert: nur endliche Proben (siehe min/max-Schleife oben) →
            // der JSON-Payload und die Kreis-Renderer sehen nie ∞/NaN.
            var raw = new List<(long T, double V)>(s.Points.Count);
            for (int i = 0; i < s.Points.Count; i++)
            {
                var (t, v) = s.Points[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;   // Lücke statt ∞/NaN
                if (raw.Count > 0) sb.Append(' ');
                sb.Append(X(t).ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(Y(v).ToString("0.#", CultureInfo.InvariantCulture));
                raw.Add((t, v));
            }
            if (raw.Count == 0) continue;                                 // Serie nur aus ∞/NaN → weglassen
            paths.Add(new SeriesPath(s.Label, s.Color, sb.ToString(), raw));
            totalPoints += raw.Count;
        }
        if (paths.Count == 0) return null;

        // 5 y-Gridlines ueber den Wertebereich.
        var yGrid = new List<GridLine>(5);
        for (int i = 0; i <= 4; i++)
        {
            double val = yLo + (yHi - yLo) * i / 4.0;
            yGrid.Add(new GridLine(Y(val), FmtValue(val)));
        }

        // 6 x-Ticks ueber die Zeitspanne.
        var xTicks = new List<TickLabel>(6);
        for (int i = 0; i <= 5; i++)
        {
            long t = xMin + (long)((xMax - xMin) * (i / 5.0));
            xTicks.Add(new TickLabel(X(t), HeimdallFmt.Ts(t)));
        }

        return new ChartGeometry(width, height, padLeft, padTop, padRight, padBottom,
            plotW, plotH, xMin, xMax, yLo, yHi, paths, yGrid, xTicks, totalPoints);
    }

    public sealed record ChartGeometry(
        int Width, int Height,
        double PadLeft, double PadTop, double PadRight, double PadBottom,
        double PlotW, double PlotH,
        long XMin, long XMax, double YLo, double YHi,
        IReadOnlyList<SeriesPath> Series,
        IReadOnlyList<GridLine> YGrid,
        IReadOnlyList<TickLabel> XTicks,
        int TotalPoints);

    // ---------------------------------------------------------------------
    // SVG-String-Erzeugung (server-gerendert; vermieden, Razor-SVG-Parsing-Pitfalls)
    // ---------------------------------------------------------------------

    /// <summary>Erzeugt das vollstaendige Liniendiagramm als SVG-String.</summary>
    public static string RenderLineSvg(ChartGeometry g, string ariaLabel)
    {
        var sb = new StringBuilder(2048);
        sb.Append("<svg viewBox=\"0 0 ").Append(g.Width).Append(' ').Append(g.Height)
          .Append("\" class=\"hmd-chart\" role=\"img\" aria-label=\"")
          .Append(Esc(ariaLabel)).Append("\" preserveAspectRatio=\"xMidYMid meet\">");

        double left = g.PadLeft, top = g.PadTop, bottom = g.PadTop + g.PlotH, right = g.PadLeft + g.PlotW;

        // y-Gridlines + Labels
        foreach (var gl in g.YGrid)
        {
            sb.Append("<line class=\"hmd-chart-grid\" x1=\"").Append(F(left))
              .Append("\" y1=\"").Append(F(gl.Y)).Append("\" x2=\"").Append(F(right))
              .Append("\" y2=\"").Append(F(gl.Y)).Append("\"/>");
            sb.Append("<text class=\"hmd-chart-label hmd-chart-ylabel\" x=\"")
              .Append(F(left - 6)).Append("\" y=\"").Append(F(gl.Y))
              .Append("\" text-anchor=\"end\" dominant-baseline=\"middle\">")
              .Append(Esc(gl.Label)).Append("</text>");
        }
        // Achsen
        sb.Append("<line class=\"hmd-chart-axis\" x1=\"").Append(F(left)).Append("\" y1=\"")
          .Append(F(top)).Append("\" x2=\"").Append(F(left)).Append("\" y2=\"").Append(F(bottom)).Append("\"/>");
        sb.Append("<line class=\"hmd-chart-axis\" x1=\"").Append(F(left)).Append("\" y1=\"")
          .Append(F(bottom)).Append("\" x2=\"").Append(F(right)).Append("\" y2=\"").Append(F(bottom)).Append("\"/>");
        // x-Ticks + Labels
        foreach (var tk in g.XTicks)
        {
            sb.Append("<line class=\"hmd-chart-axis\" x1=\"").Append(F(tk.X)).Append("\" y1=\"")
              .Append(F(bottom)).Append("\" x2=\"").Append(F(tk.X)).Append("\" y2=\"").Append(F(bottom + 4)).Append("\"/>");
            sb.Append("<text class=\"hmd-chart-label hmd-chart-xlabel\" x=\"").Append(F(tk.X))
              .Append("\" y=\"").Append(F(bottom + 18)).Append("\" text-anchor=\"middle\">")
              .Append(Esc(Trunc(tk.Label, 13))).Append("</text>");
        }
        // Linien + (wenige) Punkte. Bei vorhandenem RawPoints werden die Kreis-Punkte
        // mit data-*-Attributen (T/V/Label) angereichert → Hover-Tooltips via JS
        // (Progressive Enhancement; ohne JS bleibt das SVG unverändert nutzbar).
        bool showPts = g.TotalPoints <= 60;
        double xRange = g.XMax - g.XMin;
        double yRange = g.YHi - g.YLo;
        double Xp(long t) => g.PadLeft + (t - g.XMin) / xRange * g.PlotW;
        double Yp(double v) => g.PadTop + (1.0 - (v - g.YLo) / yRange) * g.PlotH;
        foreach (var s in g.Series)
        {
            sb.Append("<polyline class=\"hmd-chart-line\" points=\"")
              .Append(s.Points).Append("\" fill=\"none\" stroke=\"").Append(s.Color)
              .Append("\" stroke-width=\"1.5\"/>");
            if (!showPts) continue;
            if (s.RawPoints is not null)
            {
                for (int i = 0; i < s.RawPoints.Count; i++)
                {
                    var (t, v) = s.RawPoints[i];
                    sb.Append("<circle class=\"hmd-chart-pt\" cx=\"").Append(F(Xp(t)))
                      .Append("\" cy=\"").Append(F(Yp(v)))
                      .Append("\" r=\"1.8\" fill=\"").Append(s.Color)
                      .Append("\" data-t=\"").Append(t.ToString(CultureInfo.InvariantCulture))
                      .Append("\" data-v=\"").Append(v.ToString("0.####", CultureInfo.InvariantCulture))
                      .Append("\" data-label=\"").Append(Esc(s.Label)).Append("\"/>");
                }
            }
            else
            {
                foreach (var tok in s.Points.Split(' '))
                {
                    var c = tok.Split(',');
                    if (c.Length == 2) sb.Append("<circle class=\"hmd-chart-pt\" cx=\"")
                        .Append(c[0]).Append("\" cy=\"").Append(c[1])
                        .Append("\" r=\"1.8\" fill=\"").Append(s.Color).Append("\"/>");
                }
            }
        }

        // Daten-Payload für Progressive-Enhancement-Interaktion (Crosshair + Brushing):
        // <script type="application/json"> wird vom Browser NICHT ausgeführt, von JS via
        // textContent gelesen. JsonSerializer escaped <,>,& default als \uXXXX → sicher
        // im script-Element. Nur wenn mind. eine Serie RawPoints hat (sonst alt-Fallback).
        AppendChartData(sb, g, Xp, Yp);

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Hängt einen &lt;script type="application/json" class="hmd-chart-data"&gt;-Block
    /// mit Geometrie + Serienpunkten (SVG-Koordinaten + Rohwerte t/v) an das SVG an —
    /// Datenbasis für Crosshair &amp; Brushing/Zoom in <c>heimdall.js</c>. Kein JS wird
    /// ausgeführt (Content-Type application/json); ohne JS bleibt das SVG unverändert.
    /// </summary>
    private static void AppendChartData(StringBuilder sb, ChartGeometry g, Func<long, double> Xp, Func<double, double> Yp)
    {
        var withRaw = new List<SeriesPath>(g.Series.Count);
        foreach (var s in g.Series) if (s.RawPoints is not null) withRaw.Add(s);
        if (withRaw.Count == 0) return;

        var payload = new
        {
            geo = new { padLeft = g.PadLeft, padTop = g.PadTop, plotW = g.PlotW, plotH = g.PlotH, xMin = g.XMin, xMax = g.XMax },
            series = withRaw.Select(s => new
            {
                label = s.Label,
                color = s.Color,
                pts = s.RawPoints!.Select(p => new[] { Xp(p.T), Yp(p.V), (double)p.T, p.V }).ToArray()
            }).ToArray()
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        sb.Append("<script type=\"application/json\" class=\"hmd-chart-data\">")
          .Append(json).Append("</script>");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Default-Escaping bleibt aktiv (<,>,& als \uXXXX) → sicher im <script>-Element.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Erzeugt das Balkendiagramm (Histogramm-Verteilung) als SVG-String.</summary>
    public static string RenderHistSvg(IReadOnlyList<long> counts, IReadOnlyList<double> bounds, int width, int height, string? ariaLabel = null)
    {
        if (counts is null || counts.Count == 0) return string.Empty;
        const double padL = 44, padT = 14, padB = 28, padR = 8;
        double plotW = width - padL - padR, plotH = height - padT - padB;
        double barW = plotW / counts.Count;
        double maxCount = 0;
        foreach (var c in counts) if (c > maxCount) maxCount = c;

        var sb = new StringBuilder(1024);
        sb.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
          .Append("\" class=\"hmd-chart hmd-hist\" role=\"img\" aria-label=\"")
          .Append(Esc(ariaLabel ?? "Histogramm")).Append("\" preserveAspectRatio=\"xMidYMid meet\">");
        sb.Append("<line class=\"hmd-chart-grid\" x1=\"").Append(F(padL)).Append("\" y1=\"")
          .Append(F(padT)).Append("\" x2=\"").Append(F(padL + plotW)).Append("\" y2=\"").Append(F(padT)).Append("\"/>");
        sb.Append("<line class=\"hmd-chart-axis\" x1=\"").Append(F(padL)).Append("\" y1=\"")
          .Append(F(padT + plotH)).Append("\" x2=\"").Append(F(padL + plotW)).Append("\" y2=\"").Append(F(padT + plotH)).Append("\"/>");

        for (int i = 0; i < counts.Count; i++)
        {
            double bx = padL + i * barW;
            double bh = maxCount > 0 ? counts[i] * plotH / maxCount : 0;
            double by = padT + plotH - bh;
            sb.Append("<rect class=\"hmd-hist-bar\" x=\"").Append(F(bx + 1)).Append("\" y=\"")
              .Append(F(by)).Append("\" width=\"").Append(F(barW - 2)).Append("\" height=\"")
              .Append(F(bh)).Append("\"/>");
            sb.Append("<text class=\"hmd-chart-label hmd-chart-xlabel\" x=\"").Append(F(bx + barW / 2))
              .Append("\" y=\"").Append(F(padT + plotH + 16)).Append("\" text-anchor=\"middle\">")
              .Append(Esc(Trunc(BoundLabel(bounds, i), 10))).Append("</text>");
            sb.Append("<text class=\"hmd-chart-label\" x=\"").Append(F(bx + barW / 2)).Append("\" y=\"")
              .Append(F(by - 3)).Append("\" text-anchor=\"middle\">").Append(counts[i].ToString(CultureInfo.InvariantCulture)).Append("</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // Grafana-Panel-Renderer: Heatmap (server-gerendertes SVG)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Erzeugt ein Heatmap-SVG: Zeit (x) × Histogramm-Bucket (y), Farbintensität
    /// = inkrementelle Rate. <paramref name="buckets"/> aufsteigend nach Obergrenze
    /// (letztes = +Inf); <paramref name="columnTimesMs"/> die Zeitspalten. Farb-
    /// Schema Blues (transparent → kräftig), Skala sqrt (Grafana <c>exponent 0.5</c>).
    /// Größtes le oben, kleinstes unten (Grafana <c>yAxis.reverse=false</c>); Zellen
    /// mit Rate ≤ 1e-9 werden übersprungen (<c>filterValues.le</c>). Wirft nie.
    /// </summary>
    public static string RenderHeatmapSvg(
        IReadOnlyList<HeatmapBucket> buckets, IReadOnlyList<long> columnTimesMs,
        double maxValue, int width, int height, string? ariaLabel = null)
    {
        if (buckets is null || buckets.Count == 0 || columnTimesMs is null || columnTimesMs.Count == 0)
            return string.Empty;
        int nRows = buckets.Count;
        int cols = columnTimesMs.Count;
        const double padL = 52, padT = 12, padB = 26, padR = 10;
        double plotW = width - padL - padR;
        if (plotW < 20) plotW = 20;
        double rowH = (height - padT - padB) / nRows;
        if (rowH < 6) rowH = 6;
        double colW = plotW / cols;
        if (colW < 1) colW = 1;
        double plotH = rowH * nRows;
        double svgH = padT + plotH + padB;
        double bottomY = padT + plotH;
        double rightX = padL + cols * colW;

        var sb = new StringBuilder(8192);
        sb.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(F(svgH))
          .Append("\" class=\"hmd-chart hmd-heatmap\" role=\"img\" aria-label=\"")
          .Append(Esc(ariaLabel ?? "Heatmap")).Append("\" preserveAspectRatio=\"xMidYMid meet\">");

        // Achsenrahmen (links + unten).
        sb.Append("<line class=\"hmd-chart-axis\" x1=\"").Append(F(padL)).Append("\" y1=\"")
          .Append(F(padT)).Append("\" x2=\"").Append(F(padL)).Append("\" y2=\"").Append(F(bottomY)).Append("\"/>");
        sb.Append("<line class=\"hmd-chart-axis\" x1=\"").Append(F(padL)).Append("\" y1=\"")
          .Append(F(bottomY)).Append("\" x2=\"").Append(F(rightX)).Append("\" y2=\"").Append(F(bottomY)).Append("\"/>");

        // Zellen: oben = größtes le (buckets aufsteigend → Quelle nRows-1-r).
        for (int r = 0; r < nRows; r++)
        {
            var vals = buckets[nRows - 1 - r].Values;
            double y = padT + r * rowH;
            for (int c = 0; c < cols; c++)
            {
                double v = c < vals.Count ? vals[c] : 0;
                if (v <= 1e-9) continue;
                double t = maxValue > 0 ? v / maxValue : 0;
                if (t > 1) t = 1;
                t = Math.Sqrt(t);                       // exponent 0.5 (exponential scale)
                double alpha = 0.10 + 0.90 * t;
                double x = padL + c * colW;
                sb.Append("<rect class=\"hmd-heat-cell\" x=\"").Append(F(x + .5)).Append("\" y=\"")
                  .Append(F(y + .5)).Append("\" width=\"").Append(F(colW - 1)).Append("\" height=\"")
                  .Append(F(rowH - 1)).Append("\" fill=\"rgba(54,162,235,")
                  .Append(alpha.ToString("0.##", CultureInfo.InvariantCulture)).Append(")\"/>");
            }
        }

        // Y-Achsen-Labels (Bucket-Obergrenzen), je Zeile mittig.
        for (int r = 0; r < nRows; r++)
        {
            string label = buckets[nRows - 1 - r].Label;
            double y = padT + r * rowH + rowH / 2 + 3;
            sb.Append("<text class=\"hmd-chart-label hmd-chart-ylabel\" x=\"").Append(F(padL - 6))
              .Append("\" y=\"").Append(F(y)).Append("\" text-anchor=\"end\">")
              .Append(Esc(Trunc(label, 10))).Append("</text>");
        }

        // X-Achse: bis zu 5 Zeit-Ticks (HH:mm:ss) mit dünnen Gitterlinien.
        int ticks = Math.Min(5, cols);
        for (int i = 0; i < ticks; i++)
        {
            int c = ticks == 1 ? 0 : (int)((long)i * (cols - 1) / (ticks - 1));
            double x = padL + c * colW;
            sb.Append("<line class=\"hmd-chart-grid\" x1=\"").Append(F(x)).Append("\" y1=\"")
              .Append(F(padT)).Append("\" x2=\"").Append(F(x)).Append("\" y2=\"").Append(F(bottomY)).Append("\"/>");
            sb.Append("<text class=\"hmd-chart-label hmd-chart-xlabel\" x=\"").Append(F(x))
              .Append("\" y=\"").Append(F(bottomY + 15)).Append("\" text-anchor=\"middle\">")
              .Append(Esc(Trunc(TimeOnly(columnTimesMs[c] * 1_000_000L), 8))).Append("</text>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>HeimdallFmt.Ts → Uhrzeit-Anteil („HH:mm:ss.fff"); für Chart-X-Ticks.</summary>
    private static string TimeOnly(long ns)
    {
        var ts = HeimdallFmt.Ts(ns);
        int sp = ts.IndexOf(' ');
        return sp >= 0 && sp + 1 < ts.Length ? ts.Substring(sp + 1) : ts;
    }

    private static string BoundLabel(IReadOnlyList<double> bounds, int i) =>
        i < bounds.Count ? FmtValue(bounds[i]) : "+Inf";

    // ---------------------------------------------------------------------
    // Grafana-Panel-Renderer: Gauge / BarGauge / Pie (server-gerendertes SVG)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Erzeugt einen Halbkreis-Gauge (Kreisbogen) als SVG. <paramref name="value"/>
    /// wird zwischen <paramref name="min"/> und <paramref name="max"/> auf den
    /// Bogen abgebildt; entartete Bereiche (min==max, NaN) werden abgesichert.
    /// </summary>
    public static string RenderGaugeSvg(double value, double min, double max, string color, string label, int w, int h)
    {
        double cx = w / 2.0;
        double padT = 10, padB = 28, padS = 10;
        double cy = h - padB;
        double r = Math.Min(cx - padS, cy - padT);
        if (r < 4) r = 4;

        double span = max - min;
        double frac = span > 0 ? (value - min) / span : 0;
        if (double.IsNaN(frac) || double.IsInfinity(frac)) frac = 0;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;

        // Punkt auf dem Halbkreis bei Winkel a (Grad, 0=rechts, 180=links), oben.
        static (double x, double y) Pt(double cx, double cy, double r, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return (cx + r * Math.Cos(rad), cy - r * Math.Sin(rad));
        }

        var (sx, sy) = Pt(cx, cy, r, 180);                  // links
        var (ex, ey) = Pt(cx, cy, r, 180 - 180 * frac);      // Wert-Ende
        var (rx, ry) = Pt(cx, cy, r, 0);                     // rechts

        var sb = new StringBuilder(512);
        sb.Append("<svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h)
          .Append("\" class=\"hmd-chart hmd-gauge\" role=\"img\" aria-label=\"")
          .Append(Esc(label)).Append("\" preserveAspectRatio=\"xMidYMid meet\">");
        // Hintergrund-Halbkreis.
        sb.Append("<path class=\"hmd-gauge-track\" d=\"M ").Append(F(sx)).Append(' ').Append(F(sy))
          .Append(" A ").Append(F(r)).Append(' ').Append(F(r)).Append(" 0 0 1 ")
          .Append(F(rx)).Append(' ').Append(F(ry)).Append("\" fill=\"none\"/>");
        // Wert-Bogen (nur wenn frac>0, sonst leerer Pfad).
        if (frac > 0)
            sb.Append("<path class=\"hmd-gauge-value\" d=\"M ").Append(F(sx)).Append(' ').Append(F(sy))
              .Append(" A ").Append(F(r)).Append(' ').Append(F(r)).Append(" 0 0 1 ")
              .Append(F(ex)).Append(' ').Append(F(ey)).Append("\" fill=\"none\" stroke=\"")
              .Append(color).Append("\" stroke-width=\"10\" stroke-linecap=\"round\"/>");
        // Wertmittig.
        sb.Append("<text class=\"hmd-gauge-val\" x=\"").Append(F(cx)).Append("\" y=\"")
          .Append(F(cy - 6)).Append("\" text-anchor=\"middle\">").Append(Esc(FmtValue(value)))
          .Append("</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Erzeugt horizontale Balken (Bargauge) als SVG. Jede Zeile skaliert gegen
    /// das Maximum aller Zeilen; Entartungen (leer, max<=0) werden abgesichert.
    /// </summary>
    public static string RenderBarGaugeSvg(IReadOnlyList<BarGaugeRow> rows, int w, int h, string? ariaLabel = null)
    {
        if (rows is null || rows.Count == 0) return string.Empty;
        const double padL = 120, padT = 6, padB = 6, padR = 48;
        double plotW = w - padL - padR;
        if (plotW < 20) plotW = 20;
        double rowH = (h - padT - padB) / rows.Count;
        if (rowH < 14) rowH = 14;
        double barH = rowH * 0.6;
        double max = 0;
        foreach (var r in rows) if (r.Max > max) max = r.Max;
        if (max <= 0) max = 1;

        var sb = new StringBuilder(512);
        sb.Append("<svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h)
          .Append("\" class=\"hmd-chart hmd-bargauge\" role=\"img\" aria-label=\"")
          .Append(Esc(ariaLabel ?? "Bargauge")).Append("\" preserveAspectRatio=\"xMidYMid meet\">");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            double y = padT + i * rowH + (rowH - barH) / 2;
            double bw = plotW * (row.Value / max);
            if (double.IsNaN(bw) || bw < 0) bw = 0; else if (bw > plotW) bw = plotW;
            sb.Append("<text class=\"hmd-chart-label hmd-bg-label\" x=\"").Append(F(padL - 6))
              .Append("\" y=\"").Append(F(y + barH * 0.75)).Append("\" text-anchor=\"end\">")
              .Append(Esc(Trunc(row.Label, 22))).Append("</text>");
            sb.Append("<rect class=\"hmd-bg-track\" x=\"").Append(F(padL)).Append("\" y=\"")
              .Append(F(y)).Append("\" width=\"").Append(F(plotW)).Append("\" height=\"")
              .Append(F(barH)).Append("\"/>");
            sb.Append("<rect class=\"hmd-bg-fill\" x=\"").Append(F(padL)).Append("\" y=\"")
              .Append(F(y)).Append("\" width=\"").Append(F(bw)).Append("\" height=\"")
              .Append(F(barH)).Append("\" fill=\"").Append(row.Color).Append("\"/>");
            sb.Append("<text class=\"hmd-chart-label hmd-bg-val\" x=\"").Append(F(padL + plotW + 4))
              .Append("\" y=\"").Append(F(y + barH * 0.75)).Append("\" text-anchor=\"start\">")
              .Append(Esc(FmtValue(row.Value))).Append("</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Mini-Flächen-Sparkline für Stat-Kacheln (Grafana stat <c>graphMode=area</c>):
    /// ein schmales Inline-SVG mit gefüllter Fläche unter der Wertelinie. Die Farbe
    /// wird über die Tone-Klasse (<c>hmd-spark--{tone}</c>) per CSS zugewiesen, sodass
    /// Threshold-Tones (ok/warn/err/accent) konsistent zu den KPI-Karten sind.
    /// Bei &lt; 2 Punkten (Skalar/flach) entsteht ein leerer String (kein Graph).
    /// </summary>
    public static string RenderStatSparklineSvg(IReadOnlyList<(long Tms, double V)> pts, string tone, int w, int h)
    {
        if (pts is null || pts.Count < 2) return string.Empty;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var p in pts) { if (p.V < min) min = p.V; if (p.V > max) max = p.V; }
        if (!(max > min)) { min = 0; if (!(max > 0)) max = 1; }   // entartet → flache Linie bei oben

        var sb = new StringBuilder(96 + pts.Count * 10);
        sb.Append("<svg class=\"hmd-stat-spark hmd-spark--").Append(tone).Append("\" viewBox=\"0 0 ")
          .Append(w).Append(' ').Append(h).Append("\" preserveAspectRatio=\"none\" aria-hidden=\"true\">");
        double dx = (double)w / (pts.Count - 1);
        var path = new StringBuilder(pts.Count * 10);
        for (int i = 0; i < pts.Count; i++)
        {
            double x = i * dx;
            double y = h - (pts[i].V - min) / (max - min) * h;
            path.Append(i == 0 ? 'M' : 'L').Append(F(x)).Append(' ').Append(F(y)).Append(' ');
        }
        // Fläche: Linie + Schließen unten rechts → unten links.
        sb.Append("<path class=\"hmd-stat-spark-area\" d=\"").Append(path)
          .Append("L").Append(F(w)).Append(' ').Append(F(h)).Append(" L0 ").Append(F(h)).Append(" Z\"/>");
        sb.Append("<path class=\"hmd-stat-spark-line\" d=\"").Append(path).Append("\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Lane des Signal-Bandes („Wachtband“) auf der Übersicht: 60 Minuten-Buckets
    /// als Linie + Fläche über einer NULL-Basislinie (Volumen ist absolut —
    /// Skala immer 0..max, bei lauter Nullen bleibt die Grundlinie unten) plus
    /// drei dezente Gitterlinien. Liefert das gestreckte SVG
    /// (<c>preserveAspectRatio="none"</c>, volle Breite via CSS) UND den
    /// Endpunkt-Dot als HTML-<c>&lt;span&gt;</c>: Kreise im gestreckten SVG
    /// würden zu Ellipsen verzerren, ein HTML-Dot bleibt kreisrund. Der Dot
    /// trägt seine Höhe inline (Prozent), horizontal sitzt er fest am rechten
    /// Rand (der letzte Bucket ist immer „jetzt“). Bei &lt; 2 Punkten leer.
    /// </summary>
    public static string RenderBandLaneSvg(IReadOnlyList<(long Tms, double V)> pts, int w = 600, int h = 56)
    {
        if (pts is null || pts.Count < 2) return string.Empty;
        const double PadTop = 3, PadBottom = 3;
        double max = 0;
        foreach (var p in pts) { if (p.V > max) max = p.V; }
        if (!(max > 0)) max = 1;                        // lauter Nullen → Grundlinie
        double usable = h - PadTop - PadBottom;

        var sb = new StringBuilder(160 + pts.Count * 12);
        sb.Append("<svg class=\"hmd-band-lane-svg\" viewBox=\"0 0 ").Append(w).Append(' ')
          .Append(h).Append("\" preserveAspectRatio=\"none\" aria-hidden=\"true\">");
        sb.Append("<g class=\"hmd-band-grid\">");
        for (int g = 1; g <= 3; g++)
        {
            double gy = h * g / 4.0;
            sb.Append("<line x1=\"0\" x2=\"").Append(w).Append("\" y1=\"").Append(F(gy))
              .Append("\" y2=\"").Append(F(gy)).Append("\"/>");
        }
        sb.Append("</g>");

        double dx = (double)w / (pts.Count - 1);
        double baseY = h - PadBottom, lastY = baseY;
        var path = new StringBuilder(pts.Count * 12);
        for (int i = 0; i < pts.Count; i++)
        {
            double y = baseY - (pts[i].V / max) * usable;
            lastY = y;
            path.Append(i == 0 ? 'M' : 'L').Append(F(i * dx)).Append(' ').Append(F(y)).Append(' ');
        }
        // Fläche: Wertelinie + Schließen entlang der Null-Basislinie.
        sb.Append("<path class=\"hmd-band-area\" d=\"").Append(path)
          .Append("L").Append(F(w)).Append(' ').Append(F(baseY))
          .Append(" L0 ").Append(F(baseY)).Append(" Z\"/>");
        sb.Append("<path class=\"hmd-band-line\" d=\"").Append(path).Append("\"/>");
        sb.Append("</svg>");
        sb.Append("<span class=\"hmd-band-enddot\" aria-hidden=\"true\" style=\"top:")
          .Append(F(lastY / h * 100.0)).Append("%\"></span>");
        return sb.ToString();
    }

    /// <summary>
    /// Erzeugt ein Kreisdiagramm (Pie) als SVG mit rechtsseitiger Legende.
    /// Negatives/NaN werden abgesichert; die Gesamtsumme darf null sein
    /// (dann bleibt ein grauer Vollkreis).
    /// </summary>
    public static string RenderPieSvg(IReadOnlyList<PieSlice> slices, int w, int h, string? ariaLabel = null)
    {
        if (slices is null || slices.Count == 0) return string.Empty;
        double cx = w * 0.36, cy = h / 2.0;
        double r = Math.Min(cx - 8, cy - 8);
        if (r < 8) r = 8;
        double total = 0;
        foreach (var s in slices) if (s.Value > 0) total += s.Value;

        var sb = new StringBuilder(512);
        sb.Append("<svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h)
          .Append("\" class=\"hmd-chart hmd-pie\" role=\"img\" aria-label=\"")
          .Append(Esc(ariaLabel ?? "Kreisdiagramm")).Append("\" preserveAspectRatio=\"xMidYMid meet\">");

        if (total <= 0)
        {
            sb.Append("<circle class=\"hmd-pie-empty\" cx=\"").Append(F(cx)).Append("\" cy=\"")
              .Append(F(cy)).Append("\" r=\"").Append(F(r)).Append("\"/>");
        }
        else
        {
            double a = -90; // oben starten
            foreach (var s in slices)
            {
                if (s.Value <= 0) continue;
                double sweep = 360 * (s.Value / total);
                double a0 = a, a1 = a + sweep;
                a = a1;
                double r0 = a0 * Math.PI / 180.0, r1 = a1 * Math.PI / 180.0;
                double x0 = cx + r * Math.Cos(r0), y0 = cy + r * Math.Sin(r0);
                double x1 = cx + r * Math.Cos(r1), y1 = cy + r * Math.Sin(r1);
                bool large = sweep > 180;
                sb.Append("<path class=\"hmd-pie-slice\" d=\"M ").Append(F(cx)).Append(' ')
                  .Append(F(cy)).Append(" L ").Append(F(x0)).Append(' ').Append(F(y0))
                  .Append(" A ").Append(F(r)).Append(' ').Append(F(r)).Append(" 0 ")
                  .Append(large ? 1 : 0).Append(" 1 ").Append(F(x1)).Append(' ').Append(F(y1))
                  .Append(" Z\" fill=\"").Append(s.Color).Append("\"/>");
            }
        }

        // Legende rechts.
        double lx = w * 0.56, ly = 14;
        for (int i = 0; i < slices.Count; i++)
        {
            double lyi = ly + i * 18;
            if (lyi > h - 4) break;
            sb.Append("<rect class=\"hmd-legend-swatch\" x=\"").Append(F(lx)).Append("\" y=\"")
              .Append(F(lyi)).Append("\" width=\"10\" height=\"10\" fill=\"").Append(slices[i].Color).Append("\"/>");
            sb.Append("<text class=\"hmd-chart-label hmd-pie-label\" x=\"").Append(F(lx + 14))
              .Append("\" y=\"").Append(F(lyi + 9)).Append("\">").Append(Esc(Trunc(slices[i].Label, 18)))
              .Append(" (").Append(Esc(FmtValue(slices[i].Value))).Append(")</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // Format-Helfer
    // ---------------------------------------------------------------------

    /// <summary>Kompakte Wertformatierung fuer Achsen-Labels.</summary>
    public static string FmtValue(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        double a = Math.Abs(v);
        if (a >= 1e9) return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "G";
        if (a >= 1e6) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (a >= 1e3) return (v / 1e3).ToString("0.##", CultureInfo.InvariantCulture) + "k";
        if (Math.Round(v) == v && a < 1e6) return v.ToString("0", CultureInfo.InvariantCulture);
        return v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Kompakte Darstellung einer Attribut-Gruppe fuer Chart-Legenden-Labels.</summary>
    public static string FormatAttrsLabel(IReadOnlyList<AttrKv> attrs)
    {
        if (attrs is null || attrs.Count == 0) return "(keine Attribute)";
        var sb = new StringBuilder();
        for (int i = 0; i < attrs.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(attrs[i].Key).Append('=').Append(attrs[i].Value);
        }
        return sb.ToString();
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