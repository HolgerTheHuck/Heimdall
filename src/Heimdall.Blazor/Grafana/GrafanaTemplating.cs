using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Prometheus;

namespace Heimdall.Blazor.Grafana;

// ---------------------------------------------------------------------------
// Template-Variablen: Interpolation von $var/${var} in PromQL-Ausdruecken und
// Aufloesung der Dropdown-Optionen (label_values, custom). Rein, werferfrei.
// Die Variablenwerte kommen als einfache Strings (GET-Query-Parameter);
// Multi-Selektion (Komma-getrennt) wird zu einer Regex-Alternation (a|b),
// "$__all"/leer → .* (passt zu allem).
// ---------------------------------------------------------------------------

/// <summary>
/// Statische Helfer fuer Grafana-Template-Variablen.
/// </summary>
public static class GrafanaTemplating
{
    // $name, ${name} oder ${name:modifier} (Grafana-Syntax, z. B. ${percentile:value});
    // der Name beginnt mit Buchstabe/_ und enthaelt [\w]. Der optionale Modifier
    // (:value/:text/:queryparam …) wird erfasst, aber fuer den PromQL-Kontext nicht
    // ausgewertet — Heimdall speichert ohnehin den Wert (nicht den Display-Text).
    private static readonly Regex VarToken = new(
        @"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::([A-Za-z_][A-Za-z0-9_]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

    /// <summary>
    /// Ersetzt alle <c>$var</c>/<c>${var}</c>/<c>${var:modifier}</c>-Vorkommen in
    /// <paramref name="expr"/> durch die Werte aus <paramref name="vars"/>. Nicht
    /// vorhandene Variablen bleiben stehen. "<c>$__all</c>" und leere Werte werden
    /// zu "<c>.*</c>"; Komma-getrennte Multi-Werte zu "<c>a|b</c>" (Regex-Alternation).
    /// </summary>
    /// <remarks>
    /// <b>Operator-Beförderung</b> (Grafana-Semantik): steht ein Regex-Wert
    /// ("<c>.*</c>" bei All-/Leer-Auswahl oder "<c>a|b</c>" bei Multi-Auswahl)
    /// hinter einem Label-Matcher mit <c>=</c>- bzw. <c>!=</c>-Operator, wird der
    /// Operator zu <c>=~</c> bzw. <c>!~</c> befördert. Ohne dies würde
    /// <c>service_name=".*"</c> als EXAKTER Match für den Text "<c>.*</c>" gewertet
    /// und träfe keine Serie — alle Panels blieben leer. Grafana macht genau diese
    /// Beförderung bei All-/Multi-Auswahl automatisch; Heimdall reicht sie nach.
    /// Einzelwerte (kein Regex) bleiben am <c>=</c>-Operator (exakter Match).
    /// </remarks>
    public static string Interpolate(string expr, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(expr) || vars is null || vars.Count == 0) return expr ?? string.Empty;
        // Manueller Aufbau (statt Regex.Replace-Callback), damit der VOR dem Token
        // stehende =/!=-Operator bei Regex-Werten zu =~/!~ umgeschrieben werden kann.
        var sb = new StringBuilder(expr.Length + 32);
        int last = 0;
        foreach (Match m in VarToken.Matches(expr))
        {
            sb.Append(expr, last, m.Index - last);
            // Gruppe 1 = ${name}, Gruppe 2 = Modifier (ignoriert), Gruppe 3 = $name.
            string name = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[3].Value;
            if (!vars.TryGetValue(name, out var raw))
            {
                sb.Append(m.Value);                 // unbekannte Variable → Token lassen
                last = m.Index + m.Length;
                continue;
            }
            string encoded = Encode(raw);
            bool isRegex = encoded == ".*" || encoded.Contains('|');
            if (isRegex) PromoteOperator(sb);       // =/!=  →  =~/!~  (nur bei Regex-Werten)
            sb.Append(encoded);
            last = m.Index + m.Length;
        }
        sb.Append(expr, last, expr.Length - last);
        return sb.ToString();
    }

    /// <summary>
    /// Befördert einen unmittelbar vor dem einzufügenden Wert stehenden
    /// Label-Matcher-Operator: <c>="</c> → <c>=~"</c>, <c>!="</c> → <c>!~"</c>.
    /// Bereits vorhandenes <c>=~"</c>/<c>!~"</c> bleibt unangetastet. Liegt dem
    /// Wert KEIN gequoteter Matcher voraus (z. B. <c>topk($top, …)</c>), ist dies
    /// ein No-Op — die Beförderung wirkt nur innerhalb von <c>{label="…"}</c>.
    /// </summary>
    private static void PromoteOperator(StringBuilder sb)
    {
        int i = sb.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(sb[i])) i--;
        if (i < 0 || sb[i] != '"') return;              // kein gequoteter Matcher
        i--;
        while (i >= 0 && char.IsWhiteSpace(sb[i])) i--;
        if (i < 0 || sb[i] != '=') return;              // kein =/!=  (=~ wäre ~)
        if (i - 1 >= 0 && sb[i - 1] == '~') return;     // bereits =~
        if (i - 1 >= 0 && sb[i - 1] == '!')             // != → !~
        {
            sb[i] = '~';
            return;
        }
        sb.Insert(i + 1, '~');                          // = → =~
    }

    /// <summary>
    /// Formatiert eine Millisekunden-Dauer als PromQL-Duration-Literal
    /// (<c>&lt;s&gt;s</c>/<c>&lt;m&gt;m</c>/<c>&lt;h&gt;h</c>) — für die
    /// Grafana-Built-in-Variablen <c>$__interval</c>/<c>$__rate_interval</c>,
    /// die in <c>rate(…[$__rate_interval])</c>/<c>max_over_time(…[$__interval])</c>
    /// stehen und ohne Interpolation vom PromQL-Parser abgewiesen würden.
    /// </summary>
    public static string DurationLabel(long ms)
    {
        if (ms <= 0) ms = 1_000;
        if (ms < 60_000) return (ms / 1_000L).ToString(CultureInfo.InvariantCulture) + "s";
        if (ms < 3_600_000L) return (ms / 60_000L).ToString(CultureInfo.InvariantCulture) + "m";
        return (ms / 3_600_000L).ToString(CultureInfo.InvariantCulture) + "h";
    }

    /// <summary>Kodiert einen Variablenwert fuer den PromQL-Kontext.</summary>
    public static string Encode(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "$__all") return ".*";
        // Multi-Selektion (Komma-getrennt, z. B. "a,b") → Regex-Alternation.
        if (raw.Contains(','))
        {
            var parts = raw.Split(',');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(parts[i].Trim());
            }
            return sb.Length == 0 ? ".*" : sb.ToString();
        }
        return raw;
    }

    /// <summary>
    /// Loest die Dropdown-Optionen einer Template-Variablen auf.
    /// <list type="bullet">
    /// <item><c>query</c> mit <c>label_values(metric, label)</c> →
    ///   <see cref="PromEngine.ListLabelValues"/>.</item>
    /// <item><c>custom</c> → Komma-getrennte Werte aus <c>Query</c>.</item>
    /// <item><c>datasource</c> → leer (wird im UI nicht angeboten).</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ResolveOptions(
        GrafanaTemplatingVar v, PromEngine engine, long? fromNs = null, long? toNs = null)
    {
        if (v is null) return Array.Empty<string>();
        if (string.Equals(v.Type, "datasource", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        if (string.Equals(v.Type, "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(v.Query)) return Array.Empty<string>();
            var parts = v.Query.Split(',');
            var list = new List<string>(parts.Length);
            foreach (var p in parts) { var t = p.Trim(); if (t.Length > 0) list.Add(t); }
            return list;
        }

        // Default: query (label_values).
        var label = ParseLabelValuesLabel(v.Query);
        if (label is null) return Array.Empty<string>();
        return engine.ListLabelValues(label, fromNs, toNs);
    }

    /// <summary>
    /// Extrahiert den Label-Namen aus <c>label_values(&lt;selector&gt;, label)</c>
    /// (das letzte Komma-Argument vor der schließenden Klammer). null bei
    /// unerkanntem Format.
    /// </summary>
    public static string? ParseLabelValuesLabel(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = query.Trim();
        if (!q.StartsWith("label_values", StringComparison.OrdinalIgnoreCase)) return null;
        int open = q.IndexOf('(');
        int close = q.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        string inner = q.Substring(open + 1, close - open - 1);
        int comma = inner.LastIndexOf(',');
        if (comma < 0) return null;
        string label = inner.Substring(comma + 1).Trim();
        return string.IsNullOrEmpty(label) ? null : label;
    }

    /// <summary>
    /// Bestimmt den aktuell gewaehlten Wert einer Variablen: explizit aus
    /// <paramref name="selected"/> (GET-Query), sonst der Default-Wert aus
    /// dem Dashboard-Modell (<see cref="GrafanaTemplatingVar.CurrentValue"/>),
    /// sonst <c>$__all</c>.
    /// </summary>
    public static string SelectedValue(
        GrafanaTemplatingVar v, IReadOnlyDictionary<string, string>? selected)
    {
        if (selected is not null && selected.TryGetValue(v.Name, out var s) && !string.IsNullOrEmpty(s))
            return s;
        return v.CurrentValue ?? "$__all";
    }
}