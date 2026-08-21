using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// Kleiner gecachter Regex-Helper fuer PromQL-Label-Matcher (=~, !~) und
// label_replace/label_join. Prom-Patterns sind meist simpel und wiederholen
// sich; Caching spart die Compilierung. Zeitlimit schutzt vor katastrophalen
// Backtracking-Patterns.
// ---------------------------------------------------------------------------

internal static class SafeRegex
{
    private static readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string input, string pattern)
        => Get(pattern).IsMatch(input);

    public static string? Replace(string input, string pattern, string replacement)
        => Get(pattern).Replace(input, replacement);

    public static Match Match(string input, string pattern)
        => Get(pattern).Match(input);

    private static Regex Get(string pattern)
    {
        Regex r;
        lock (_cache)
        {
            if (!_cache.TryGetValue(pattern, out r!))
            {
                // PromQL-Regex sind RE2-aehnlich (gesamttreffer via find); .NET Regex
                // ist gut genug. Anchoring: Prom matcht, wenn irgendwo ein Treffer
                // existiert (ungespiegelt) — also keine expliziten Anker.
                r = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                _cache[pattern] = r;
            }
        }
        return r;
    }
}