using System;
using System.IO;
using System.Linq;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer IHeimdallQuery.ListSpans (SQLite-Backend): Filterung nach Zeitfenster,
/// Span-Kind, MinStatusCode sowie Limit/Offset-Paging. SpanRow liefert die rohen
/// Spalten incl. attrs_json für die App-seitige Controller/Endpoint-Aggregation.
/// </summary>
public class ListSpansTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long Ns(long ticks) => (ticks - UnixEpochTicks) * 100L;
    private static long NowNs => Ns(DateTimeOffset.UtcNow.UtcTicks);

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-listspans-" + Guid.NewGuid().ToString("N") + ".db");

    private static byte[] Tid(int seed) { var b = new byte[16]; b[0] = 0x5e; b[15] = (byte)seed; return b; }
    private static byte[] Sid(int seed) { var b = new byte[8]; b[0] = (byte)(seed >> 8); b[7] = (byte)seed; return b; }

    private static HSpan Span(int seed, HSpanKind kind, long startNs, bool error, string route)
        => new(Tid(seed), Sid(seed), null, route, kind, startNs, startNs + 1_000_000,
            error ? HStatusCode.Error : HStatusCode.Ok, error ? "boom" : null,
            new[] { new HAttribute("http.route", route) },
            Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            new HResource(new[] { new HAttribute("service.name", "shop") }),
            new HScope("api", "1.0", Array.Empty<HAttribute>()));

    private static SQLiteTelemetrySink NewSink(string path)
        => new(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0 });

    [Fact]
    public void ListSpans_Ohne_Filter_Liefert_Alle()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var t0 = NowNs;
            sink.WriteSpans(new[]
            {
                Span(1, HSpanKind.Server,   t0,         false, "/api/users"),
                Span(2, HSpanKind.Internal, t0 + 1_000,  false, "db.query"),
                Span(3, HSpanKind.Server,   t0 + 2_000,  true,  "/api/orders"),
            });

            var all = sink.ListSpans(new SpanFilter { Limit = 100 });
            Assert.Equal(3, all.Count);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ListSpans_Filter_Kind_Server()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var t0 = NowNs;
            sink.WriteSpans(new[]
            {
                Span(1, HSpanKind.Server,   t0,        false, "/api/users"),
                Span(2, HSpanKind.Internal, t0 + 1_000, false, "db.query"),
                Span(3, HSpanKind.Client,   t0 + 2_000, false, "http.out"),
                Span(4, HSpanKind.Server,   t0 + 3_000, false, "/api/orders"),
            });

            var servers = sink.ListSpans(new SpanFilter { Kind = (int)HSpanKind.Server, Limit = 100 });
            Assert.Equal(2, servers.Count);
            Assert.All(servers, s => Assert.Equal((int)HSpanKind.Server, s.Kind));
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ListSpans_Filter_Zeitfenster()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var t0 = NowNs;
            sink.WriteSpans(new[]
            {
                Span(1, HSpanKind.Server, t0,             false, "/a"),  // vor Fenster
                Span(2, HSpanKind.Server, t0 + 1_000_000,  false, "/b"),  // im Fenster
                Span(3, HSpanKind.Server, t0 + 2_000_000,  false, "/c"),  // im Fenster
                Span(4, HSpanKind.Server, t0 + 9_000_000,  false, "/d"),  // nach Fenster
            });

            var inWindow = sink.ListSpans(new SpanFilter
            {
                FromUnixNano = t0 + 500_000,
                ToUnixNano = t0 + 5_000_000,
                Kind = (int)HSpanKind.Server,
                Limit = 100,
            });
            Assert.Equal(2, inWindow.Count);
            Assert.All(inWindow, s => Assert.True(s.StartUnixNano >= t0 + 500_000 && s.StartUnixNano <= t0 + 5_000_000));
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ListSpans_Filter_MinStatusCode_Liefert_Nur_Fehler()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var t0 = NowNs;
            sink.WriteSpans(new[]
            {
                Span(1, HSpanKind.Server, t0,        false, "/ok1"),
                Span(2, HSpanKind.Server, t0 + 1_000, true,  "/err1"),
                Span(3, HSpanKind.Server, t0 + 2_000, false, "/ok2"),
                Span(4, HSpanKind.Server, t0 + 3_000, true,  "/err2"),
            });

            var errors = sink.ListSpans(new SpanFilter { Kind = (int)HSpanKind.Server, MinStatusCode = (int)HStatusCode.Error, Limit = 100 });
            Assert.Equal(2, errors.Count);
            Assert.All(errors, s => Assert.Equal((int)HStatusCode.Error, s.StatusCode));
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ListSpans_Limit_Offset_Paging()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            var t0 = NowNs;
            var spans = new HSpan[5];
            for (int i = 0; i < 5; i++)
                spans[i] = Span(i + 1, HSpanKind.Server, t0 + i * 1_000_000, false, "/r" + i);
            sink.WriteSpans(spans);

            // DESC nach start → erste Seite (Limit 2, Offset 0) = die zwei neuesten.
            var page1 = sink.ListSpans(new SpanFilter { Kind = (int)HSpanKind.Server, Limit = 2, Offset = 0 });
            Assert.Equal(2, page1.Count);
            // zweite Seite = die nächsten zwei.
            var page2 = sink.ListSpans(new SpanFilter { Kind = (int)HSpanKind.Server, Limit = 2, Offset = 2 });
            Assert.Equal(2, page2.Count);
            // keine Überlappung zwischen den Seiten.
            Assert.Empty(page1.Select(s => s.SpanId).Intersect(page2.Select(s => s.SpanId)));
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ListSpans_Attrs_Roundtrip_Fuer_Aggregation()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSink(path);
            sink.WriteSpans(new[]
            {
                Span(1, HSpanKind.Server, NowNs, false, "/api/users"),
            });

            var rows = sink.ListSpans(new SpanFilter { Limit = 10 });
            Assert.Single(rows);
            // attrs_json ist roundtripped — Grundlage für die App-seitige Aggregation.
            Assert.Contains("http.route", rows[0].AttrsJson);
            Assert.Contains("/api/users", rows[0].AttrsJson);
        }
        finally { if (File.Exists(path)) try { File.Delete(path); } catch { } }
    }
}