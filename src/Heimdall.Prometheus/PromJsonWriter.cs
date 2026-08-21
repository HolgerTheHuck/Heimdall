using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// Prom-typisierte JSON-Serialisierung. Prom-Konventionen:
//   - Zeitstempel als Unix-Float-Sekunden (number),
//   - Messwerte als JSON-Strings (Präzision/NaN-Sicherheit),
//   - Envelope: {"status":"success"|"error","data":<...>,["errorType",["warnings"]]}.
// Handgeschrieben (keine Drittabhängigkeit) — die Shapes sind klein und stabil.
// ---------------------------------------------------------------------------

internal static class PromJsonWriter
{
    // === Ergebniss-Shapes ==================================================
    public static string QueryResult(PromResult r)
    {
        var sb = new StringBuilder(1024);
        switch (r.Kind)
        {
            case PromResultKind.Vector:
                sb.Append("{\"resultType\":\"vector\",\"result\":");
                WriteVector(sb, r.Vector);
                sb.Append('}');
                break;
            case PromResultKind.Matrix:
                sb.Append("{\"resultType\":\"matrix\",\"result\":");
                WriteMatrix(sb, r.Matrix);
                sb.Append('}');
                break;
            case PromResultKind.Scalar:
                sb.Append("{\"resultType\":\"scalar\",\"result\":");
                WriteScalar(sb, r.Scalar);
                sb.Append('}');
                break;
            case PromResultKind.String:
                sb.Append("{\"resultType\":\"string\",\"result\":");
                WriteStringResult(sb, r.String);
                sb.Append('}');
                break;
        }
        return sb.ToString();
    }

    private static void WriteVector(StringBuilder sb, InstantVector? v)
    {
        if (v is null) { sb.Append("[]"); return; }
        sb.Append('[');
        for (int i = 0; i < v.Samples.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var s = v.Samples[i];
            sb.Append("{\"metric\":");
            WriteLabels(sb, s.Labels, includeName: true);
            sb.Append(",\"value\":").Append('[').Append(MsToSec(s.TimestampMs)).Append(',').Append(Val(s.Value)).Append("]}");
        }
        sb.Append(']');
    }

    private static void WriteMatrix(StringBuilder sb, Matrix? m)
    {
        if (m is null) { sb.Append("[]"); return; }
        sb.Append('[');
        for (int i = 0; i < m.Series.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var rs = m.Series[i];
            sb.Append("{\"metric\":");
            WriteLabels(sb, rs.Labels, includeName: true);
            sb.Append(",\"values\":[");
            for (int j = 0; j < rs.Points.Count; j++)
            {
                if (j > 0) sb.Append(',');
                var p = rs.Points[j];
                sb.Append('[').Append(MsToSec(p.TimestampMs)).Append(',').Append(Val(p.Value)).Append(']');
            }
            sb.Append("]}");
        }
        sb.Append(']');
    }

    private static void WriteScalar(StringBuilder sb, ScalarResult? s)
    {
        if (s is null) { sb.Append("[]"); return; }
        sb.Append('[').Append(MsToSec(s.TimestampMs)).Append(',').Append(Val(s.Value)).Append(']');
    }

    private static void WriteStringResult(StringBuilder sb, StringResult? s)
    {
        if (s is null) { sb.Append("[]"); return; }
        sb.Append('[').Append(MsToSec(s.TimestampMs)).Append(',');
        AppendJsonString(sb, s.Value);
        sb.Append(']');
    }

    // === Discovery-Shapes ==================================================
    public static string StringArray(IReadOnlyList<string> values)
    {
        var sb = new StringBuilder(values.Count * 16);
        sb.Append('[');
        for (int i = 0; i < values.Count; i++) { if (i > 0) sb.Append(','); AppendJsonString(sb, values[i]); }
        sb.Append(']');
        return sb.ToString();
    }

    public static string SeriesArray(IReadOnlyList<SeriesLabels> series)
    {
        var sb = new StringBuilder(series.Count * 64);
        sb.Append('[');
        for (int i = 0; i < series.Count; i++) { if (i > 0) sb.Append(','); WriteLabels(sb, series[i], includeName: true); }
        sb.Append(']');
        return sb.ToString();
    }

    public static string Metadata(IReadOnlyDictionary<string, IReadOnlyList<MetricMeta>> meta)
    {
        var sb = new StringBuilder(meta.Count * 96);
        sb.Append('{');
        int i = 0;
        foreach (var kv in meta)
        {
            if (i++ > 0) sb.Append(',');
            AppendJsonString(sb, kv.Key);
            sb.Append(":[");
            for (int j = 0; j < kv.Value.Count; j++)
            {
                if (j > 0) sb.Append(',');
                var m = kv.Value[j];
                sb.Append("{\"type\":"); AppendJsonString(sb, m.Type);
                sb.Append(",\"help\":"); AppendJsonString(sb, m.Help);
                sb.Append(",\"unit\":"); AppendJsonString(sb, m.Unit);
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append('}');
        return sb.ToString();
    }

    public static string BuildInfoJson(IReadOnlyDictionary<string, string> info)
    {
        var sb = new StringBuilder(128);
        sb.Append('{');
        int i = 0;
        foreach (var kv in info)
        {
            if (i++ > 0) sb.Append(',');
            AppendJsonString(sb, kv.Key); sb.Append(':'); AppendJsonString(sb, kv.Value);
        }
        sb.Append('}');
        return sb.ToString();
    }

    // === Envelope ==========================================================
    /// <summary>Erfolgs-Envelope: {"status":"success","data":&lt;data&gt;,"warnings":[]}.</summary>
    public static string Success(string dataJson, IReadOnlyList<string>? warnings = null)
    {
        var sb = new StringBuilder(dataJson.Length + 48);
        sb.Append("{\"status\":\"success\",\"data\":").Append(dataJson);
        if (warnings is not null && warnings.Count > 0)
        {
            sb.Append(",\"warnings\":");
            sb.Append(StringArray(warnings));
        }
        else sb.Append(",\"warnings\":[]");
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Fehler-Envelope: {"status":"error","errorType":"...","error":"..."}.</summary>
    public static string Error(string errorType, string message)
    {
        var sb = new StringBuilder(message.Length + 64);
        sb.Append("{\"status\":\"error\",\"errorType\":"); AppendJsonString(sb, errorType);
        sb.Append(",\"error\":"); AppendJsonString(sb, message);
        sb.Append('}');
        return sb.ToString();
    }

    // === Helfer ============================================================
    private static void WriteLabels(StringBuilder sb, SeriesLabels labels, bool includeName)
    {
        sb.Append('{');
        bool first = true;
        foreach (var kv in labels)
        {
            if (!includeName && kv.Key == "__name__") continue;
            if (!first) sb.Append(',');
            AppendJsonString(sb, kv.Key); sb.Append(':'); AppendJsonString(sb, kv.Value);
            first = false;
        }
        sb.Append('}');
    }

    private static string MsToSec(long ms) => (ms / 1000.0).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Wert als JSON-String (Prom-Konvention); NaN/+Inf/-Inf als Prom-Strings.</summary>
    private static string Val(double v)
    {
        if (double.IsNaN(v)) return "\"NaN\"";
        if (double.IsPositiveInfinity(v)) return "\"+Inf\"";
        if (double.IsNegativeInfinity(v)) return "\"-Inf\"";
        return "\"" + v.ToString("R", CultureInfo.InvariantCulture) + "\"";
    }

    /// <summary>Minimaler JSON-String-Encoder (RFC 8259: ", \, und Steuerzeichen).</summary>
    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        if (string.IsNullOrEmpty(s)) { sb.Append('"'); return; }
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
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