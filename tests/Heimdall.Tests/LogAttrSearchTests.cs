using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Heimdall;
using Heimdall.Storage.SQLite;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer die index-gestuetzte Attribut-Feldsuche (Logs). Deckt Log- UND
/// Resource-Attribute (service.name liegt in resource_json), die Operatoren
/// =/!=/=~/!~, Key-Normalisierung (_ ↔ .) und Kombination mit Body-Volltext +
/// Severity + Zeit. SQLite = index-gestuetzt (heim_log_attrs). Beweist die
/// Semantik des optionalen <c>LogSearch.AttrFilters</c>. (1.0: SQLite-only;
/// das Walhalla-Backend kehrt als NuGet-Konsument zurueck.)
/// </summary>
public class LogAttrSearchTests
{
    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static long NowNs => (DateTimeOffset.UtcNow.UtcTicks - UnixEpochTicks) * 100L;

    // Drei Logs: A/C service=shop, B service=billing; Status 500 fuer B/C.
    //   A: shop, 200, "order placed for alice", Info
    //   B: billing, 500, "payment failed for bob", Error
    //   C: shop, 500, "db timeout in query", Error
    private static IReadOnlyList<HLogRecord> SampleLogs()
    {
        long t = NowNs;
        return new[]
        {
            Log(t,       HSeverity.Info,  "order placed for alice", "shop",     "/api/orders",  200L),
            Log(t + 1,   HSeverity.Error, "payment failed for bob",  "billing",  "/api/billing", 500L),
            Log(t + 2,   HSeverity.Error, "db timeout in query",    "shop",     "/api/orders",  500L),
        };
    }
    private static HLogRecord Log(long t, HSeverity sev, string body, string svc, string route, long status)
    {
        var res = new HResource(new[] { new HAttribute("service.name", svc) });
        var attrs = new[]
        {
            new HAttribute("http.route", route),
            new HAttribute("http.response.status_code", status),
        };
        return new HLogRecord(t, sev, sev.ToString().ToUpperInvariant(), body, null, null, attrs, res, null);
    }

    private static IReadOnlyList<string> Bodies(IReadOnlyList<LogRow> rows) =>
        rows.Select(r => r.Body ?? "").ToList();

    // === SQLite (index-gestuetzt) =========================================
    [Fact]
    public void SQLite_AttrEq_ResourceAttr_Trifft()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("service.name", "=", "shop") }, Limit = 100 });
        var bodies = Bodies(hits);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("order placed for alice", bodies);
        Assert.Contains("db timeout in query", bodies);
        Assert.DoesNotContain("billing", string.Join(",", bodies));
    }

    [Fact]
    public void SQLite_AttrEq_LogAttr_Statuscode_Trifft()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("http.response.status_code", "=", "500") }, Limit = 100 });
        var bodies = Bodies(hits);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("payment failed for bob", bodies);
        Assert.Contains("db timeout in query", bodies);
    }

    [Fact]
    public void SQLite_AttrNe_Strict_ErfordertAttrVorhanden()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("service.name", "!=", "billing") }, Limit = 100 });
        var bodies = Bodies(hits);
        Assert.Equal(2, bodies.Count);          // A, C (haben service.name, Wert != billing); B ausgeschlossen
        Assert.Contains("order placed for alice", bodies);
        Assert.Contains("db timeout in query", bodies);
    }

    [Fact]
    public void SQLite_AttrNe_SchliesstWertAus()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("service.name", "!=", "shop") }, Limit = 100 });
        var bodies = Bodies(hits);
        Assert.Single(bodies);
        Assert.Contains("payment failed for bob", bodies[0]);   // B (service.name=billing)
    }

    [Fact]
    public void SQLite_AttrRegex_TrifftUndNegiert()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var pos = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("http.route", "=~", "/billing$") }, Limit = 100 });
        Assert.Single(pos);
        Assert.Contains("payment failed for bob", pos[0].Body);

        var neg = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("http.route", "!~", "/billing$") }, Limit = 100 });
        Assert.Equal(2, neg.Count);             // A, C (haben route, matcht nicht /billing$)
    }

    [Fact]
    public void SQLite_KeyNormalisierung_UnterstrichTrifftPunkt()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("service_name", "=", "shop") }, Limit = 100 });
        Assert.Equal(2, hits.Count);            // service_name == service.name (Resource-Attr)
    }

    [Fact]
    public void SQLite_MehrereAttrFilter_AND()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch
        {
            AttrFilters = new[]
            {
                new AttrFilter("service.name", "=", "shop"),
                new AttrFilter("http.response.status_code", "=", "500"),
            },
            Limit = 100,
        });
        var bodies = Bodies(hits);
        Assert.Single(bodies);
        Assert.Contains("db timeout in query", bodies[0]);
    }

    [Fact]
    public void SQLite_AttrFilter_KombiniertMit_BodyFts()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch
        {
            Text = "timeout",
            AttrFilters = new[] { new AttrFilter("service.name", "=", "shop") },
            Limit = 100,
        });
        var bodies = Bodies(hits);
        Assert.Single(bodies);
        Assert.Contains("db timeout in query", bodies[0]);
    }

    [Fact]
    public void SQLite_AttrFilter_KombiniertMit_Severity()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch
        {
            MinSeverity = (int)HSeverity.Error,
            AttrFilters = new[] { new AttrFilter("service.name", "=", "shop") },
            Limit = 100,
        });
        var bodies = Bodies(hits);
        Assert.Single(bodies);                  // nur C (shop + Error)
        Assert.Contains("db timeout in query", bodies[0]);
    }

    [Fact]
    public void SQLite_FehlendesAttr_LiefertNullTreffer_Strict()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch { AttrFilters = new[] { new AttrFilter("nonexistent.attr", "=", "x") }, Limit = 100 });
        Assert.Empty(hits);                     // kein Log hat das Attr → 0 (kein lenient-Fallback)
    }

    [Fact]
    public void SQLite_AttrFiltersNull_AltesVerhaltenUnveraendert()
    {
        using var sink = NewSql();
        sink.WriteLogs(SampleLogs());
        var hits = sink.SearchLogs(new LogSearch { Text = "payment", Limit = 100 });
        Assert.Single(hits);                     // Body-FTS weiterhin, kein Attr-Filter
    }

    // === Helfer ===========================================================
    private static SQLiteTelemetrySink NewSql()
    {
        var path = Path.Combine(Path.GetTempPath(), "heimdall-attr-sql-" + Guid.NewGuid().ToString("N") + ".db");
        return new SQLiteTelemetrySink(new SQLiteTelemetryOptions { DataPath = path, RetentionDays = 0, WalMode = false });
    }
}