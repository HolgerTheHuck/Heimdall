using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Heimdall.Storage.SQLite;

/// <summary>
/// Schlanke JSON-Serialisierung fuer Attribut-/Event-/Link-Listen (shared-utility
/// fuer das SQLite-Backend; baugleich zur Walhalla-Variante, damit beide Backends
/// identische JSON-Spalte produzieren). Keine Reflexion, keine Zusaetzpaket.
/// </summary>
internal static class HeimdallJson
{
    public static string WriteAttributes(IReadOnlyList<HAttribute>? attrs)
    {
        if (attrs is null || attrs.Count == 0) return "{}";
        var sb = new StringBuilder(attrs.Count * 24);
        sb.Append('{');
        for (int i = 0; i < attrs.Count; i++)
        {
            var a = attrs[i];
            if (a.IsEmpty) continue;
            if (sb.Length > 1) sb.Append(',');
            WriteString(sb, a.Key);
            sb.Append(':');
            WriteValue(sb, a.Value);
        }
        sb.Append('}');
        return sb.ToString();
    }

    public static string WriteSpanEvents(IReadOnlyList<HSpanEvent>? events)
    {
        if (events is null || events.Count == 0) return "[]";
        var sb = new StringBuilder(events.Count * 48);
        sb.Append('[');
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            WriteString(sb, "ts"); sb.Append(':'); sb.Append(e.TimeUnixNano.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            WriteString(sb, "name"); sb.Append(':'); WriteString(sb, e.Name); sb.Append(',');
            WriteString(sb, "attrs"); sb.Append(':'); sb.Append(WriteAttributes(e.Attributes));
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string WriteSpanLinks(IReadOnlyList<HSpanLink>? links)
    {
        if (links is null || links.Count == 0) return "[]";
        var sb = new StringBuilder(links.Count * 64);
        sb.Append('[');
        for (int i = 0; i < links.Count; i++)
        {
            var l = links[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            WriteString(sb, "trace"); sb.Append(':'); WriteString(sb, ToHex(l.TraceId)); sb.Append(',');
            WriteString(sb, "span"); sb.Append(':'); WriteString(sb, ToHex(l.SpanId)); sb.Append(',');
            WriteString(sb, "attrs"); sb.Append(':'); sb.Append(WriteAttributes(l.Attributes));
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string WriteLongs(IReadOnlyList<long>? xs)
    {
        if (xs is null || xs.Count == 0) return "[]";
        var sb = new StringBuilder(xs.Count * 12);
        sb.Append('[');
        for (int i = 0; i < xs.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(xs[i].ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string WriteDoubles(IReadOnlyList<double>? xs)
    {
        if (xs is null || xs.Count == 0) return "[]";
        var sb = new StringBuilder(xs.Count * 16);
        sb.Append('[');
        for (int i = 0; i < xs.Count; i++)
        {
            if (i > 0) sb.Append(',');
            // Nicht-endliche Werte als JSON-String emitieren: das +Inf-Overflow-
            // Bound eines OTel-Histogramms ist double.PositiveInfinity, und
            // ToString("R") liefert dafür das Literal „Infinity" — KEIN gültiges
            // JSON. JsonDocument.Parse scheitert daran, ExplicitBounds ginge
            // verloren, und jeder Histogramm-Bucket käme le="+Inf" →
            // histogram_quantile = NaN. Strings („+Inf"/-Inf/NaN) sind JSON-
            // valide und werden von ParseDoubles zurückgemappt.
            double d = xs[i];
            if (double.IsNaN(d)) sb.Append("\"NaN\"");
            else if (double.IsPositiveInfinity(d)) sb.Append("\"+Inf\"");
            else if (double.IsNegativeInfinity(d)) sb.Append("\"-Inf\"");
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string ToHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        const string hex = "0123456789abcdef";
        var sb = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(hex[bytes[i] >> 4]);
            sb.Append(hex[bytes[i] & 0xF]);
        }
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s); break;
            case byte[] bytes: WriteString(sb, ToHex(bytes)); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
            case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
            case DateTime dt: WriteString(sb, dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dto: WriteString(sb, dto.ToString("O", CultureInfo.InvariantCulture)); break;
            default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""); break;
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}