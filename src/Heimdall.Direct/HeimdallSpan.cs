using System;
using System.Collections.Generic;
using Heimdall;

namespace Heimdall.Direct;

/// <summary>
/// Tracer-Implementierung: erzeugt <see cref="HeimdallSpan"/>-Instanzen und
/// verkettet sie automatisch mit dem aktuellen Span des Async-Flows (Parent).
/// </summary>
internal sealed class HeimdallTracer : IHeimdallTracer
{
    private readonly HeimdallHub _hub;
    private readonly HScope? _scope;

    public HeimdallTracer(HeimdallHub hub, HScope? scope)
    {
        _hub = hub;
        _scope = scope;
    }

    public IHeimdallSpan StartSpan(string name, HSpanKind kind = HSpanKind.Internal, IHeimdallSpan? parent = null)
    {
        if (HeimdallRecording.IsSuppressed) return HeimdallNoop.Tracer.StartSpan(name, kind, parent);
        // Impliziter Parent: aktueller Span des Async-Flows, wenn kein expliziter uebergeben.
        var p = parent as HeimdallSpan ?? _hub.CurrentSpan;
        return new HeimdallSpan(_hub, _scope, name, kind, p);
    }
}

/// <summary>
/// Aktiver Span. Sammelt Attribute/Events/Status, setzt sich waehrend seiner
/// Lebensdauer als Current-Span (AsyncLocal) und schreibt beim End einen
/// fertigen <see cref="HSpan"/> in den Sink. End/Dispose sind idempotent.
/// </summary>
internal sealed class HeimdallSpan : IHeimdallSpan
{
    private readonly HeimdallHub _hub;
    private readonly HScope? _scope;
    private readonly HeimdallSpan? _parent;
    private readonly string _name;
    private readonly HSpanKind _kind;
    private readonly long _startNs;
    private readonly byte[] _traceId;
    private readonly byte[] _spanId;
    private readonly List<HAttribute> _attrs = new();
    private readonly List<HSpanEvent> _events = new();
    private HStatusCode _status = HStatusCode.Unset;
    private string? _statusMsg;
    private HeimdallSpan? _previousCurrent;  // fuer Restore beim End
    private int _ended;                        // 0 = aktiv, 1 = beendet

    public HeimdallSpan(HeimdallHub hub, HScope? scope, string name, HSpanKind kind, HeimdallSpan? parent)
    {
        _hub = hub;
        _scope = scope;
        _parent = parent;
        _name = name;
        _kind = kind;
        _startNs = NowNs();

        // Trace-ID: vom Parent uebernehmen (gleicher Trace), sonst neu erzeugen.
        // Span-ID: immer neu.
        _traceId = parent is not null ? (byte[])parent._traceId.Clone() : RandomId(16);
        _spanId = RandomId(8);

        _previousCurrent = hub.CurrentSpan;
        hub.CurrentSpan = this;
    }

    public byte[] TraceId => _traceId;
    public byte[] SpanId => _spanId;

    public void SetAttribute(string key, object? value)
    {
        if (_ended != 0 || HeimdallRecording.IsSuppressed) return;
        if (string.IsNullOrEmpty(key)) return;
        _attrs.Add(new HAttribute(key, value));
    }

    public void AddEvent(string name, params HAttribute[] attributes)
    {
        if (_ended != 0 || HeimdallRecording.IsSuppressed) return;
        _events.Add(new HSpanEvent(NowNs(), name ?? string.Empty,
            attributes is null || attributes.Length == 0 ? HAttributes.Empty : attributes));
    }

    public void SetStatus(HStatusCode code, string? message = null)
    {
        if (_ended != 0) return;
        _status = code;
        _statusMsg = message;
    }

    public void End()
    {
        if (System.Threading.Interlocked.Exchange(ref _ended, 1) != 0) return;

        var endNs = NowNs();
        _hub.CurrentSpan = _previousCurrent;   // Parent-Kontext restaurieren

        if (HeimdallRecording.IsSuppressed) return;

        var span = new HSpan(
            _traceId, _spanId,
            _parent is null ? null : _parent._spanId,
            _name, _kind,
            _startNs, endNs,
            _status, _statusMsg,
            _attrs.Count == 0 ? HAttributes.Empty : _attrs.ToArray(),
            _events.Count == 0 ? System.Array.Empty<HSpanEvent>() : _events.ToArray(),
            System.Array.Empty<HSpanLink>(),
            _hub.Resource,
            _scope);

        _hub.WriteSpan(span);
    }

    public void Dispose() => End();

    // --- Helfer ------------------------------------------------------------

    private static long NowNs() => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    private static readonly long UnixEpochTicks =
        new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    private static byte[] RandomId(int len)
    {
        var b = new byte[len];
        System.Random.Shared.NextBytes(b);
        return b;
    }
}