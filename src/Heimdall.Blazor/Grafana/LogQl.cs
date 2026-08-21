using System;
using System.Collections.Generic;

namespace Heimdall.Blazor.Grafana;

// ---------------------------------------------------------------------------
// Minimal LogQL-Teilparser fuer Loki-Logs-Panels. Heimdall wertet Logs nicht
// ueber Loki aus, sondern ueber den eigenen Log-Store (IHeimdallQuery); daher
// wird der LogQL-Ausdruck nur soweit zerlegt, wie fuer die Abbildung auf eine
// LogSearch + in-Memory-Filter noetig:
//   * Stream-Selector  {label="wert", k=~"re.*"}  → Label-Matcher
//   * Zeilenfilter     |= "text"  != "x"  |~ "re"  !~ "re"  → Text-Filter
// Komplexere LogQL (Parser-Stage, |= json, Format/Unwrap, Aggregationen wie
// rate/count_over_time) wird nicht unterstuetzt — der Ausdruck bleibt best-
// effort, unerkannnte Reste werden ignoriert. Wirft nie (→ leeres Resultat).
// ---------------------------------------------------------------------------

/// <summary>Ein Label-Matcher im Stream-Selector.
/// <c>Op</c> ist <c>=</c>/<c>!=</c>/<c>=~</c>/<c>!~</c>.</summary>
public sealed record LogQlMatcher(string Key, string Op, string Value);

/// <summary>Ein Zeilenfilter.
/// <c>Op</c> ist <c>|=</c>/<c>!=</c>/<c>|~</c>/<c>!~</c> (Loki-Semantik:
/// <c>|=</c>=case-sensitive contains, <c>|~</c>=Regex match).</summary>
public sealed record LogQlFilter(string Op, string Value);

/// <summary>Zerlegter LogQL-Ausdruck: Stream-Selector + Zeilenfilter.</summary>
public sealed record LogQlQuery(IReadOnlyList<LogQlMatcher> Stream, IReadOnlyList<LogQlFilter> Lines);

/// <summary>Statischer LogQL-Teilparser (best-effort, wirft nie).</summary>
public static class LogQl
{
    /// <summary>Leeres Resultat (keine Selector-/Zeilenfilter).</summary>
    public static LogQlQuery Empty { get; } =
        new(Array.Empty<LogQlMatcher>(), Array.Empty<LogQlFilter>());

    /// <summary>
    /// Zerlegt einen LogQL-Ausdruck in Stream-Selector und Zeilenfilter. Bei
    /// Syntaxfehlern wird ein leeres Resultat geliefert — die UI darf durch ein
    /// boeses Target nie gelegt werden.
    /// </summary>
    public static LogQlQuery Parse(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return Empty;
        try { return ParseCore(expr); }
        catch { return Empty; }
    }

    // --- Kern --------------------------------------------------------------
    private static LogQlQuery ParseCore(string expr)
    {
        int i = 0;
        var stream = new List<LogQlMatcher>();
        var lines = new List<LogQlFilter>();
        SkipWs(expr, ref i);
        if (i < expr.Length && expr[i] == '{')
        {
            stream = ParseStream(expr, ref i);
            SkipWs(expr, ref i);
        }
        while (i < expr.Length)
        {
            var f = ParseLineFilter(expr, ref i);
            if (f is null) break;
            lines.Add(f);
            SkipWs(expr, ref i);
        }
        return new LogQlQuery(stream, lines);
    }

    private static List<LogQlMatcher> ParseStream(string expr, ref int i)
    {
        i++;   // '{' verbrauchen
        var list = new List<LogQlMatcher>();
        for (; ; )
        {
            SkipWs(expr, ref i);
            if (i >= expr.Length) break;
            if (expr[i] == '}') { i++; break; }
            string key = ReadIdent(expr, ref i);
            if (key.Length == 0) break;
            SkipWs(expr, ref i);
            string op = ReadMatcherOp(expr, ref i);
            SkipWs(expr, ref i);
            string val = ReadString(expr, ref i);
            list.Add(new LogQlMatcher(key, op, val));
            SkipWs(expr, ref i);
            if (i < expr.Length && expr[i] == ',') { i++; continue; }
            if (i < expr.Length && expr[i] == '}') { i++; break; }
            break;
        }
        return list;
    }

    private static LogQlFilter? ParseLineFilter(string expr, ref int i)
    {
        if (i + 1 >= expr.Length) return null;
        char a = expr[i], b = expr[i + 1];
        string? op = (a, b) switch
        {
            ('|', '=') => "|=",
            ('|', '~') => "|~",
            ('!', '=') => "!=",
            ('!', '~') => "!~",
            _ => null,
        };
        if (op is null) return null;
        i += 2;
        SkipWs(expr, ref i);
        string val = ReadString(expr, ref i);
        return new LogQlFilter(op, val);
    }

    // --- Token-Helfer ------------------------------------------------------
    private static string ReadIdent(string expr, ref int i)
    {
        int start = i;
        while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_' || expr[i] == '.'))
            i++;
        return expr.Substring(start, i - start);
    }

    private static string ReadMatcherOp(string expr, ref int i)
    {
        if (i + 1 < expr.Length && expr[i] == '!' && expr[i + 1] == '=') { i += 2; return "!="; }
        if (i + 1 < expr.Length && expr[i] == '!' && expr[i + 1] == '~') { i += 2; return "!~"; }
        if (i + 1 < expr.Length && expr[i] == '=' && expr[i + 1] == '~') { i += 2; return "=~"; }
        if (i < expr.Length && expr[i] == '=') { i += 1; return "="; }
        return "";
    }

    /// <summary>
    /// Liest einen String-Literal: doppelt-quoted (mit \" \\ \n \t \r) oder
    /// backtick-quoted (Loki-Raw-String, keine Escapes). Leerstring bei
    /// fehlendem/ungueltigem Quote.
    /// </summary>
    private static string ReadString(string expr, ref int i)
    {
        if (i >= expr.Length) return "";
        char q = expr[i];
        if (q != '"' && q != '`') return "";
        i++;
        var sb = new System.Text.StringBuilder();
        while (i < expr.Length && expr[i] != q)
        {
            if (q == '"' && expr[i] == '\\' && i + 1 < expr.Length)
            {
                char n = expr[i + 1];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
                i += 2;
                continue;
            }
            sb.Append(expr[i]);
            i++;
        }
        if (i < expr.Length) i++;   // schließendes Quote
        return sb.ToString();
    }

    private static void SkipWs(string expr, ref int i)
    {
        while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++;
    }
}