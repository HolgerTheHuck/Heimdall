using System;
using System.Globalization;

namespace Heimdall.Blazor;

// Kleine Format-Helfer fuer die UI: Unix-Nanosekunden -> Lesbarkeit.
internal static class HeimdallFmt
{
    private static readonly long EpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>
    /// Unix-Nanosekunden -> lokale Zeit "yyyy-MM-dd HH:mm:ss.fffzzz". Der
    /// Offset-Suffix (z. B. +02:00) ist explizit, damit Anwender in nicht-UTC-
    /// Server-Zonen erkennen, dass die Anzeige Server-lokal ist — sonst wirken
    /// alle Zeiten verschoben, wenn Server≠User-Zone.
    /// </summary>
    public static string Ts(long ns)
    {
        if (ns <= 0) return "—";
        try { return new DateTime(EpochTicks + ns / 100, DateTimeKind.Utc).ToLocalTime()
                          .ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture); }
        catch { return ns.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>Nanosekunden-Dauer -> menschlich (ns/µs/ms/s).</summary>
    public static string Dur(long ns)
    {
        if (ns < 0) return "—";
        if (ns < 1_000) return ns.ToString(CultureInfo.InvariantCulture) + " ns";
        double us = ns / 1000.0;
        if (us < 1_000) return us.ToString("0.##", CultureInfo.InvariantCulture) + " µs";
        double ms = us / 1000.0;
        if (ms < 1_000) return ms.ToString("0.##", CultureInfo.InvariantCulture) + " ms";
        return (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>OTel-Severity-Int -> Textmarke.</summary>
    public static string Sev(int s) => s switch
    {
        1 => "TRACE",
        5 => "DEBUG",
        9 => "INFO",
        13 => "WARN",
        17 => "ERROR",
        21 => "FATAL",
        _ => s.ToString(CultureInfo.InvariantCulture),
    };

    public static string Truncate(string? s, int n) =>
        s is null ? string.Empty : (s.Length <= n ? s : s.Substring(0, n) + "…");
}