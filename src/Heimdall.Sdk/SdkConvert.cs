using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Heimdall;

namespace Heimdall.Sdk;

// ---------------------------------------------------------------------------
// Konvertiert OpenTelemetry-SDK-Typen (Activity / LogRecord / Metric) in das
// kanonische Heimdall-Modell. Alle Zeitstempel -> Unix-Nanosekunden, IDs -> rohe
// Byte-Arrays. Rein funktional, ohne Zustand.
// ---------------------------------------------------------------------------
internal static class SdkConvert
{
    private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    internal static long ToUnixNano(DateTime dt)
    {
        var ticks = dt.Kind == DateTimeKind.Utc ? dt.Ticks : dt.ToUniversalTime().Ticks;
        return (ticks - UnixEpochTicks) * 100L;
    }

    internal static long ToUnixNano(DateTimeOffset dt) => (dt.UtcTicks - UnixEpochTicks) * 100L;

    internal static byte[] HexBytes(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            b[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return b;
    }

    private static readonly string ZeroTrace = "00000000000000000000000000000000";
    private static readonly string ZeroSpan = "0000000000000000";

    internal static byte[]? TraceIdBytes(ActivityTraceId tid)
    {
        var s = tid.ToHexString();
        if (string.IsNullOrEmpty(s) || s == ZeroTrace) return null;
        return HexBytes(s);
    }

    internal static byte[]? SpanIdBytes(ActivitySpanId sid)
    {
        var s = sid.ToHexString();
        if (string.IsNullOrEmpty(s) || s == ZeroSpan) return null;
        return HexBytes(s);
    }

    internal static HSpanKind MapKind(ActivityKind k) => k switch
    {
        ActivityKind.Server => HSpanKind.Server,
        ActivityKind.Client => HSpanKind.Client,
        ActivityKind.Producer => HSpanKind.Producer,
        ActivityKind.Consumer => HSpanKind.Consumer,
        _ => HSpanKind.Internal,
    };

    internal static HStatusCode MapStatus(ActivityStatusCode s) => s switch
    {
        ActivityStatusCode.Ok => HStatusCode.Ok,
        ActivityStatusCode.Error => HStatusCode.Error,
        _ => HStatusCode.Unset,
    };

    internal static IReadOnlyList<HAttribute> MapTags(IEnumerable<KeyValuePair<string, object?>>? tags)
    {
        if (tags is null) return HAttributes.Empty;
        var list = new List<HAttribute>();
        foreach (var kv in tags)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            list.Add(new HAttribute(kv.Key, kv.Value));
        }
        return list.Count == 0 ? HAttributes.Empty : list;
    }

    internal static IReadOnlyList<HSpanEvent> MapEvents(IEnumerable<ActivityEvent>? events)
    {
        if (events is null) return Array.Empty<HSpanEvent>();
        var list = new List<HSpanEvent>();
        foreach (var e in events)
            list.Add(new HSpanEvent(ToUnixNano(e.Timestamp), e.Name, MapTags(e.Tags)));
        return list.Count == 0 ? Array.Empty<HSpanEvent>() : list;
    }

    internal static IReadOnlyList<HSpanLink> MapLinks(IEnumerable<ActivityLink>? links)
    {
        if (links is null) return Array.Empty<HSpanLink>();
        var list = new List<HSpanLink>();
        foreach (var l in links)
        {
            var tid = TraceIdBytes(l.Context.TraceId);
            var sid = SpanIdBytes(l.Context.SpanId);
            if (tid is null || sid is null) continue;
            list.Add(new HSpanLink(tid, sid, MapTags(l.Tags)));
        }
        return list.Count == 0 ? Array.Empty<HSpanLink>() : list;
    }
}