using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Heimdall;
using Heimdall.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer Service-/Version-Discovery (ListServiceNames/ListServiceVersions,
/// Basis der UI-Dropdowns) und die dazugehoerigen Filter (LogSearch.ServiceName/
/// ServiceVersion, TraceFilter.ServiceName/ServiceVersion). Deckt: UNION ueber
/// Logs UND Spans (heim_log_attrs + heim_span_attrs), Paar-Semantik von
/// (service.name, service.version) auf derselben Zeile, Zeitraum-Respektierung,
/// Backfill der Span-Attr-Tabelle fuer Bestands-DBs und die Umstellung des
/// Trace-Service-Filters von Substring-LIKE auf exakten Index-Match.
/// </summary>
public class ServiceFilterTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "heimdall-svcfilter-" + Guid.NewGuid().ToString("N") + ".db");

    private static SQLiteTelemetrySink NewSql(string path) =>
        new(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0, WalMode = false });

    private static HResource Res(string svc, string? ver = null) =>
        ver is null
            ? new HResource(new[] { new HAttribute("service.name", svc) })
            : new HResource(new[] { new HAttribute("service.name", svc), new HAttribute("service.version", ver) });

    private static HLogRecord Log(long t, string body, string svc, string? ver = null) =>
        new(t, HSeverity.Info, "INFO", body, null, null, Array.Empty<HAttribute>(), Res(svc, ver), null);

    // Deterministische 16-Byte-Trace-ID / 8-Byte-Span-ID (Muster SQLiteTelemetrySinkTests).
    private static (byte[] id, string hex) Tid(int seed)
    {
        var b = new byte[16];
        b[0] = 0xa1; b[15] = (byte)seed;
        return (b, Hex(b));
    }
    private static (byte[] id, string hex) Sid(int seed)
    {
        var b = new byte[8];
        b[0] = (byte)(seed >> 8); b[7] = (byte)seed;
        return (b, Hex(b));
    }
    private static string Hex(byte[] b)
    {
        const string h = "0123456789abcdef";
        var sb = new System.Text.StringBuilder(b.Length * 2);
        for (int i = 0; i < b.Length; i++) { sb.Append(h[b[i] >> 4]); sb.Append(h[b[i] & 0xF]); }
        return sb.ToString();
    }

    private static HSpan Span(int traceSeed, int spanSeed, string svc, string? ver = null, long? start = null)
    {
        var s = start ?? NowNs;
        return new HSpan(Tid(traceSeed).id, Sid(spanSeed).id, null, "op", HSpanKind.Server,
            s, s + 1_000_000, HStatusCode.Ok, null,
            Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(),
            Res(svc, ver), null);
    }

    private static IReadOnlyList<string> Bodies(IReadOnlyList<LogRow> rows) =>
        rows.Select(r => r.Body ?? "").ToList();

    // === Discovery ========================================================

    [Fact]
    public void ListServiceNames_VereinigtLogsUndSpans()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteLogs(new[] { Log(NowNs, "msg-a", "shop") });
            sink.WriteSpans(new[] { Span(1, 1, "traceonly") });   // nur Traces, keine Logs

            var names = sink.ListServiceNames();
            Assert.Contains("shop", names);
            Assert.Contains("traceonly", names);                   // Span-seitiger Zweig der UNION
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ListServiceVersions_NurVersionenDesService_AusLogsUndSpans()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteLogs(new[]
            {
                Log(NowNs,     "msg-shop-v1", "shop",    "v1"),
                Log(NowNs + 1, "msg-shop-v2", "shop",    "v2"),
                Log(NowNs + 2, "msg-bill-v1", "billing", "v1"),
            });
            sink.WriteSpans(new[] { Span(1, 1, "shop", "v3") });   // Span-Only-Version

            Assert.Equal(new[] { "v1", "v2", "v3" }, sink.ListServiceVersions("shop"));
            Assert.Equal(new[] { "v1" }, sink.ListServiceVersions("billing"));
            Assert.Empty(sink.ListServiceVersions(""));            // leerer Service -> leer
            Assert.Empty(sink.ListServiceVersions(null!));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ListServiceNames_RespektiertZeitraum()
    {
        var path = NewDbPath();
        try
        {
            long old = NowNs - 10_000_000_000L;                    // ~10 s zurück (alt genug)
            using var sink = NewSql(path);
            sink.WriteLogs(new[]
            {
                Log(old,     "msg-old", "legacysvc"),
                Log(NowNs,   "msg-new", "freshsvc"),
            });

            Assert.DoesNotContain("legacysvc", sink.ListServiceNames(NowNs - 1_000_000_000L, null));
            Assert.Contains("freshsvc", sink.ListServiceNames(NowNs - 1_000_000_000L, null));
            // Global (kein Zeitraum): beide.
            var all = sink.ListServiceNames();
            Assert.Contains("legacysvc", all);
            Assert.Contains("freshsvc", all);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void SpanAttrs_Backfill_NachNeustart_ErneuterNeustartIdempotent()
    {
        // Bestands-DB simulieren: Spans geschrieben, Attr-Tabelle geleert (Stand
        // vor Anlage der Trigger) -> Bootstrap-Backfill muss die Zeilen neu
        // expandieren; ein drittes Oeffnen darf nicht duplizieren (INSERT OR IGNORE).
        var path = NewDbPath();
        try
        {
            using (var sink1 = NewSql(path))
            {
                sink1.WriteSpans(new[] { Span(1, 1, "legacysvc") });
            }
            using (var raw = new SqliteConnection("Data Source=" + path))
            {
                raw.Open();
                using var cmd = new SqliteCommand("DELETE FROM heim_span_attrs", raw);
                cmd.ExecuteNonQuery();
            }

            using (var sink2 = NewSql(path))
            {
                Assert.Contains("legacysvc", sink2.ListServiceNames());   // Backfill griff
            }
            using (var sink3 = NewSql(path))
            {
                Assert.Contains("legacysvc", sink3.ListServiceNames());  // idempotent, kein Crash
            }
        }
        finally { TryDelete(path); }
    }

    // === Filter: Logs =====================================================

    [Fact]
    public void SearchLogs_ServiceUndVersion_PaarSemantik()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteLogs(new[]
            {
                Log(NowNs,     "msg-shop-v1", "shop",    "v1"),
                Log(NowNs + 1, "msg-shop-v2", "shop",    "v2"),
                Log(NowNs + 2, "msg-bill-v1", "billing", "v1"),
            });

            // Paar: nur das shop/v2-Log (nicht billing/v1, trotz gleichem Versions-Wert).
            var pair = sink.SearchLogs(new LogSearch { ServiceName = "shop", ServiceVersion = "v2", Limit = 100 });
            Assert.Equal(new[] { "msg-shop-v2" }, Bodies(pair));

            // Nur Service: beide shop-Logs.
            var svc = sink.SearchLogs(new LogSearch { ServiceName = "shop", Limit = 100 });
            Assert.Equal(2, svc.Count);

            // Unbekannter Service: leer (kein lenient-Fallback).
            Assert.Empty(sink.SearchLogs(new LogSearch { ServiceName = "nope", Limit = 100 }));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void SearchLogs_ServiceFilter_AND_mit_LogQL_AttrFilter()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteLogs(new[]
            {
                Log(NowNs,     "msg-500", "shop", "v1"),
                Log(NowNs + 1, "msg-200", "shop", "v1"),
                Log(NowNs + 2, "msg-bill", "billing", "v1"),
            });
            sink.WriteLogs(new[]
            {
                new HLogRecord(NowNs, HSeverity.Info, "INFO", "msg-attr-500", null, null,
                    new[] { new HAttribute("http.response.status_code", 500L) }, Res("shop", "v1"), null),
            });

            // Dropdown-Filter (ServiceName) UND LogQL-Feldfilter (AttrFilters) — AND.
            var hits = sink.SearchLogs(new LogSearch
            {
                ServiceName = "shop",
                AttrFilters = new[] { new AttrFilter("http.response.status_code", "=", "500") },
                Limit = 100,
            });
            Assert.Equal(new[] { "msg-attr-500" }, Bodies(hits));
        }
        finally { TryDelete(path); }
    }

    // === Filter: Traces ===================================================

    [Fact]
    public void ListTraces_ServiceUndVersion_PaarAufGleichemSpan()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteSpans(new[]
            {
                Span(1, 1, "shop", "v1"),
                Span(2, 2, "shop", "v2"),
                Span(3, 3, "billing", "v1"),
                // T4: zwei Spans — einer mit service.name=shop (ohne version), einer
                // mit service.version=v2 (anderer Name). Paar-Semantik darf NICHT
                // treffen: Name und Version muessen auf DEMSELBEN Span sitzen.
                Span(4, 4, "shop", null),
                Span(4, 5, "billing", "v2"),
            });

            var pair = sink.ListTraces(new TraceFilter { ServiceName = "shop", ServiceVersion = "v2", Limit = 100 });
            // Nur T2 — T4 (Name auf Span 4, Version auf Span 5) trifft NICHT, da
            // Name und Version auf keinem einzelnen Span zusammen vorkommen.
            Assert.Single(pair);
            Assert.Equal(Tid(2).hex, pair[0].TraceId);

            var shopV1 = sink.ListTraces(new TraceFilter { ServiceName = "shop", ServiceVersion = "v1", Limit = 100 });
            Assert.Single(shopV1);
            Assert.Equal(Tid(1).hex, shopV1[0].TraceId);

            var shopOnly = sink.ListTraces(new TraceFilter { ServiceName = "shop", Limit = 100 });
            Assert.Equal(3, shopOnly.Count);   // T1, T2 und T4 (mindestens ein Shop-Span)
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ListTraces_ServiceName_ExaktStattSubstring()
    {
        // Semantik-Aenderung zum frueheren resource_json LIKE '%svc%': exakter
        // Match. Teil-Strings (alte Bookmarks, Praefixe) treffen nicht mehr.
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteSpans(new[] { Span(1, 1, "shop") });

            Assert.Single(sink.ListTraces(new TraceFilter { ServiceName = "shop", Limit = 100 }));
            Assert.Empty(sink.ListTraces(new TraceFilter { ServiceName = "sho", Limit = 100 }));    // Praefix
            Assert.Empty(sink.ListTraces(new TraceFilter { ServiceName = "hop", Limit = 100 }));    // Infix
        }
        finally { TryDelete(path); }
    }

    // === Filter: Multi-Select (ServiceNames) ==============================

    [Fact]
    public void SearchLogs_ServiceNames_IN_OderInnerhalbDerListe()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteLogs(new[]
            {
                Log(NowNs,     "msg-shop-v1", "shop",    "v1"),
                Log(NowNs + 1, "msg-bill-v2", "billing", "v2"),
                Log(NowNs + 2, "msg-worker",  "worker"),
                Log(NowNs + 3, "msg-shop-v9", "shop",    "v9"),
            });

            // ODER innerhalb der Liste: shop- UND billing-Logs, kein worker.
            var both = sink.SearchLogs(new LogSearch { ServiceNames = new[] { "shop", "billing" }, Limit = 100 });
            Assert.Equal(3, both.Count);
            Assert.DoesNotContain("msg-worker", Bodies(both));

            // UND mit Version bleibt Paar-Semantik auf derselben Zeile.
            var pair = sink.SearchLogs(new LogSearch { ServiceNames = new[] { "shop", "billing" }, ServiceVersion = "v2", Limit = 100 });
            Assert.Equal(new[] { "msg-bill-v2" }, Bodies(pair));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void SearchLogs_ServiceNames_NimmtVorrangVorEinzelServiceName()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteLogs(new[]
            {
                Log(NowNs,     "msg-shop", "shop"),
                Log(NowNs + 1, "msg-bill", "billing"),
            });

            // Nicht-leere Liste hat Vorrang (dokumentierte Rangfolge).
            var hits = sink.SearchLogs(new LogSearch { ServiceName = "shop", ServiceNames = new[] { "billing" }, Limit = 100 });
            Assert.Equal(new[] { "msg-bill" }, Bodies(hits));

            // Leere Liste = kein Filter -> Fallback auf ServiceName.
            var fallback = sink.SearchLogs(new LogSearch { ServiceName = "shop", ServiceNames = Array.Empty<string>(), Limit = 100 });
            Assert.Equal(new[] { "msg-shop" }, Bodies(fallback));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ListTraces_ServiceNames_IN_UndVersionPaarSemantik()
    {
        var path = NewDbPath();
        try
        {
            using var sink = NewSql(path);
            sink.WriteSpans(new[]
            {
                Span(1, 1, "shop",    "v1"),
                Span(2, 2, "billing", "v1"),
                Span(3, 3, "worker",  null),
                Span(4, 4, "billing", "v2"),
            });

            // ODER innerhalb der Liste: T1, T2 und T4 — nicht T3 (worker).
            var both = sink.ListTraces(new TraceFilter { ServiceNames = new[] { "shop", "billing" }, Limit = 100 });
            Assert.Equal(3, both.Count);
            Assert.DoesNotContain(Tid(3).hex, both.Select(t => t.TraceId));

            // UND mit Version: Paar auf demselben Span — nur T4 (billing/v2).
            var pair = sink.ListTraces(new TraceFilter { ServiceNames = new[] { "shop", "billing" }, ServiceVersion = "v2", Limit = 100 });
            Assert.Single(pair);
            Assert.Equal(Tid(4).hex, pair[0].TraceId);
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) try { File.Delete(path); } catch { }
    }
}