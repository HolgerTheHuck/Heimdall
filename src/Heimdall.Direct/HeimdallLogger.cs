using System;
using Heimdall;

namespace Heimdall.Direct;

/// <summary>Logger: emittiert Log-Eintraege, verknuepft mit dem aktiven Span-Kontext.</summary>
internal sealed class HeimdallLogger : IHeimdallLogger
{
    private readonly HeimdallHub _hub;
    private readonly HScope? _scope;

    public HeimdallLogger(HeimdallHub hub, HScope? scope)
    {
        _hub = hub;
        _scope = scope;
    }

    public void Emit(HSeverity severity, string? body, params HAttribute[] attributes)
    {
        if (HeimdallRecording.IsSuppressed) return;
        var cur = _hub.CurrentSpan;
        var rec = new HLogRecord(
            NowNs(), severity, severity.ToString(), body,
            cur is null ? null : (byte[])cur.TraceId.Clone(),
            cur is null ? null : (byte[])cur.SpanId.Clone(),
            attributes is null || attributes.Length == 0 ? HAttributes.Empty : attributes,
            _hub.Resource, _scope);
        _hub.WriteLog(rec);
    }

    private static long NowNs() => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;
    private static readonly long UnixEpochTicks =
        new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
}