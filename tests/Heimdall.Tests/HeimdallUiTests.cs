#if NET10_0
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Heimdall;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// UI-Tests für die professionelle Oberfläche (Blazor static SSR): Landing-Route
/// (Übersicht), Traces-Verschiebung nach /traces, aktive Nav-Markierung, Trace-
/// Wasserfall, Chart-Datenpayload (Crosshair/Brushing-Basis) und modernisierte
/// Zeitbereich-Steuerung. Bootet den Stand-alone-Host via <see cref="HostBootTestBase"/>
/// auf einer isolierten Temp-DB und seedet gezielt Spans/Metriken über den Sink aus DI.
/// </summary>
public class HeimdallUiTests : HostBootTestBase
{
    private static readonly byte[] TraceId = new byte[] { 0xb1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    private static readonly byte[] RootSpanId = new byte[] { 0x10, 0, 0, 0, 0, 0, 0, 0x01 };
    private static readonly byte[] ChildSpanId = new byte[] { 0x20, 0, 0, 0, 0, 0, 0, 0x02 };

    // === Landing / Routing (Block 3) =====================================

    [Fact]
    public async Task GetOtelRoot_RendertUebersicht()
    {
        var resp = await Client.GetAsync("/otel");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Razor HTML-kodiert @I18n.T-Ausgaben (Umlaute → Entity, XSS-sicher); um
        // den sichtbaren Text zu prüfen, wird der Body vorher dekodiert.
        var body = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        Assert.Contains("Übersicht", body);
        // Leere Test-DB → geführter Empty-State (Block 7) statt Navcards.
        Assert.Contains("hmd-empty-state", body);
    }

    [Fact]
    public async Task GetOtelRootMitDaten_RendertNavcards()
    {
        // Sobald Telemetrie liegt, zeigt die Landing die Quick-Nav-Kacheln.
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteMetrics(new[] { MetricPoint("orders", System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L, 1) });

        var body = await (await Client.GetAsync("/otel")).Content.ReadAsStringAsync();
        Assert.Contains("hmd-navcards", body);
    }

    [Fact]
    public async Task GetTracesRoute_RendertTracesSeite()
    {
        // TracesPage wurde von "/" nach "/traces" verschoben; Root ist nun Home.
        var resp = await Client.GetAsync("/otel/traces");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        Assert.Contains("<h1>Traces</h1>", body);
        Assert.Contains("Name enthält", body);   // Traces-Filter-Label
    }

    [Fact]
    public async Task GetOtelRoot_IstNichtTracesSeite()
    {
        // Regressionssicherung: Root darf nicht mehr die Traces-Tabelle sein.
        var resp = await Client.GetAsync("/otel");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<h1>Traces</h1>", body);
    }

    // === Aktive Nav-Markierung (Block 2) =================================

    [Fact]
    public async Task Nav_MarkiertAktivenTabLogs()
    {
        var body = await (await Client.GetAsync("/otel/logs")).Content.ReadAsStringAsync();
        // Traces/Logs/Metriken hängen unter dem Drilldown-Tab (Gruppe); auf /logs
        // ist der Drilldown-Tab aktiv, nicht ein eigener Logs-Tab.
        Assert.Contains("href=\"/otel/drilldown\" aria-current=\"page\"", body);
        // Übersicht-Tab (Root) darf auf /logs nicht aktiv sein.
        Assert.DoesNotContain("href=\"/otel\" aria-current=\"page\"", body);
    }

    [Fact]
    public async Task Nav_MarkiertAktivenTabMetriken()
    {
        var body = await (await Client.GetAsync("/otel/metrics")).Content.ReadAsStringAsync();
        // Metriken hängen unter dem Drilldown-Tab; auf /metrics ist dieser aktiv.
        Assert.Contains("href=\"/otel/drilldown\" aria-current=\"page\"", body);
    }

    [Fact]
    public async Task Nav_MarkiertUebersichtAufRoot()
    {
        var body = await (await Client.GetAsync("/otel")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/otel\" aria-current=\"page\"", body);
    }

    // === Trace-Wasserfall (Block 5) ======================================

    [Fact]
    public async Task TraceDetail_RendertWasserfallFuerMultiSpanTrace()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var nowNs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        sink.WriteSpans(new[]
        {
            new HSpan(TraceId, RootSpanId, null, "GET /api/orders", HSpanKind.Server,
                nowNs, nowNs + 500_000_000, HStatusCode.Ok, null,
                System.Array.Empty<HAttribute>(), System.Array.Empty<HSpanEvent>(), System.Array.Empty<HSpanLink>(), null, null),
            new HSpan(TraceId, ChildSpanId, RootSpanId, "db.query", HSpanKind.Client,
                nowNs + 50_000_000, nowNs + 200_000_000, HStatusCode.Ok, null,
                System.Array.Empty<HAttribute>(), System.Array.Empty<HSpanEvent>(), System.Array.Empty<HSpanLink>(), null, null),
        });

        var tid = Query.ListTraces(new TraceFilter { Limit = 10 })[0].TraceId;
        var resp = await Client.GetAsync("/otel/trace/" + tid);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("hmd-waterfall", body);
        Assert.Contains("hmd-waterfall-bar", body);
        // Zwei Spans → zwei Wasserfall-Balken.
        Assert.Equal(2, CountOccurrences(body, "hmd-waterfall-bar"));
    }

    // === Chart-Datenpayload (Block 4: Crosshair/Brushing-Basis) ==========

    [Fact]
    public async Task MetricsChart_EnthaeltDatenpayload()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var baseNs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L - 120_000_000_000L;
        sink.WriteMetrics(new[]
        {
            MetricPoint("orders", baseNs, 10),
            MetricPoint("orders", baseNs + 60_000_000_000L, 25),
            MetricPoint("orders", baseNs + 120_000_000_000L, 40),
        });

        var resp = await Client.GetAsync("/otel/metrics?name=orders");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("hmd-chart-data", body);   // <script type="application/json"> mit Serien
    }

    // === Zeitbereich-Steuerung (Block 6) =================================

    [Fact]
    public async Task TimeRange_EnthaeltRelativeButtonsUndRefreshSelect()
    {
        var resp = await Client.GetAsync("/otel/traces?preset=1h");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("hmd-range-btn", body);
        Assert.Contains("name=\"preset\" value=\"1h\"", body);
        Assert.Contains("aria-pressed=\"true\"", body);   // 1h aktiv
        Assert.Contains("name=\"refresh\"", body);        // Auto-Refresh-Select
        Assert.Contains("datetime-local", body);          // Benutzerdefiniert-Felder
    }

    /// <summary>
    /// Regression für <c>BadHttpRequestException: Failed to bind parameter "Nullable&lt;long&gt;
    /// from" from ""</c>: die hidden from/to-Inputs aus <see cref="HeimdallTimeRange"/>
    /// werden bei Preset-Submit leer gesendet (<c>from=&amp;to=</c>). Minimal-APIs binden
    /// den leeren String an <c>long?</c> nicht als null, sondern werfen. Handler binden
    /// daher <c>string?</c> und parsen tolerant (<c>ParseNs</c>). Alle betroffenen Routen
    /// müssen mit leeren from/to 200 liefern.
    /// </summary>
    [Theory]
    [InlineData("/otel/logs?sev=17&from=&to=")]              // User-Repro: Logs auf WARN
    [InlineData("/otel/logs?from=&to=&preset=1h")]
    [InlineData("/otel/traces?from=&to=&preset=24h")]
    [InlineData("/otel/metrics?from=&to=&name=orders")]
    [InlineData("/otel/dashboard?from=&to=")]
    [InlineData("/otel/endpoints?from=&to=&preset=1h")]
    [InlineData("/otel/dashboards/heimdall-overview?from=&to=")]
    public async Task LeereFromToParameter_Liefert200StattBindungsfehler(string url)
    {
        var resp = await Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    /// Regression für das gleiche Bindungs-Problem bei <c>int?</c>-Query-Parametern:
    /// das /logs-Filterformular schickt <c>sev=</c> (leer, „alle"-Option) und
    /// <c>limit=</c> bei JEDEM Submit mit — auch beim Klick auf einen Zeit-Button.
    /// Direkte <c>int?</c>-Bindung wirft bei leerem String → HTTP 400 (Crash).
    /// Handler binden daher <c>string?</c> und parsen tolerant (<c>ParseInt</c>).
    /// </summary>
    [Theory]
    [InlineData("/otel/logs?sev=&limit=&from=&to=&preset=1h")]      // User-Repro: Zeit-Button auf /logs
    [InlineData("/otel/logs?sev=&limit=200&text=&q=")]
    [InlineData("/otel/logs?svc=&ver=&limit=&from=&to=&preset=1h")] // Selects schicken „alle" als Leerstring mit
    [InlineData("/otel/traces?limit=&offset=&from=&to=&preset=24h")]
    [InlineData("/otel/traces?svc=&ver=&limit=&from=&to=&preset=24h")]
    [InlineData("/otel/metrics?limit=&from=&to=&name=orders")]
    [InlineData("/otel/endpoints?limit=&from=&to=&preset=1h")]
    [InlineData("/otel/alerts?limit=&from=&to=&preset=1h")]
    public async Task LeereIntParameter_Liefert200StattBindungsfehler(string url)
    {
        var resp = await Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    /// Metriken-Seite ohne Namen: Discovery-Modus. Früher war „orders" als Default
    /// hartkodiert (altes Beispiel), so dass die Seite stets „Keine Messpunkte für
    /// orders" zeigte und ein gelöschter Name sofort wiederkehrte. Jetzt listet sie
    /// die im Zeitraum verfügbaren Metrik-Namen als anklickbare Links.
    /// </summary>
    [Fact]
    public async Task MetricsSeite_OhneName_ListetVerfuegbareMetrikNamen()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        // 60s in der Vergangenheit: NowUnixNano() trunkiert auf Sekunden, daher muss
        // der Seed-Zeitstempel sicher innerhalb des 1h-Default-Fensters liegen.
        var nowNs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L - 60_000_000_000L;
        sink.WriteMetrics(new[] { MetricPoint("orders", nowNs, 1) });

        var body = System.Net.WebUtility.HtmlDecode(await (await Client.GetAsync("/otel/metrics")).Content.ReadAsStringAsync());
        Assert.Contains("Verfügbare Metriken", body);   // Discovery-Modus statt „orders"-Default
        Assert.Contains("orders", body);                 // Name als anklickbarer Link gelistet
    }

    /// <summary>
    /// Dashboard ohne Request-Counter: Discovery-Modus. Früher war „orders" als Default
    /// hartkodiert (altes Demo), so dass das Request-Feld stets „orders" zeigte, der Wert
    /// nach Löschen sofort wiederkehrte und die KPIs leer blieben („orders" existiert in
    /// echten Apps nicht). Jetzt listet die Seite die verfügbaren Metrik-Namen als
    /// anklickbare Links — analog zur Metriken-Seite.
    /// </summary>
    [Fact]
    public async Task DashboardSeite_OhneRequests_ListetVerfuegbareMetrikNamen()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var nowNs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L - 60_000_000_000L;
        sink.WriteMetrics(new[] { MetricPoint("myapp.requests", nowNs, 1) });

        var body = System.Net.WebUtility.HtmlDecode(await (await Client.GetAsync("/otel/dashboard")).Content.ReadAsStringAsync());
        Assert.Contains("Verfügbare Metriken", body);   // Discovery-Modus statt „orders"-Default
        Assert.Contains("myapp.requests", body);         // Name als anklickbarer Link gelistet
        Assert.DoesNotContain("orders", body);           // alter hartkodierter Default weg
    }

    // === Helfer ==========================================================

    /// <summary>
    /// LogQL-Feldsuche auf der nativen Logs-Seite: <c>?q={service.name="shop"}</c>
    /// schraenkt die Ergebnisse index-gestuetzt auf Logs dieses Service ein
    /// (Resource-Attr, beweist den heim_log_attrs-Index im Host). Seeded zwei
    /// Logs (shop + billing) und prueft, dass nur das shop-Log gerendert wird.
    /// </summary>
    [Fact]
    public async Task LogsSeite_LogQlFeldfilter_ServiceName()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        // 60s in der Vergangenheit: NowUnixNano() trunkiert auf Sekunden, daher
        // muss der Seed-Zeitstempel sicher unter dem To-Bound des 1h-Fensters liegen.
        var nowNs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L - 60_000_000_000L;
        sink.WriteLogs(new[]
        {
            new HLogRecord(nowNs, HSeverity.Info, "INFO", "order placed for alice", null, null,
                System.Array.Empty<HAttribute>(),
                new HResource(new[] { new HAttribute("service.name", "shop") }), null),
            new HLogRecord(nowNs + 1, HSeverity.Info, "INFO", "payment failed for bob", null, null,
                System.Array.Empty<HAttribute>(),
                new HResource(new[] { new HAttribute("service.name", "billing") }), null),
        });

        var body = await (await Client.GetAsync("/otel/logs?q=" + System.Uri.EscapeDataString("{service.name=\"shop\"}"))).Content.ReadAsStringAsync();
        Assert.Contains("order placed for alice", body);
        Assert.DoesNotContain("payment failed for bob", body);
    }

    /// <summary>
    /// LogQL Feld-Regex + Body-Volltext kombiniert:
    /// <c>?q={http.route=~"^/api/.*"} |= "timeout"</c> trifft nur das Log mit
    /// passender Route UND Body-Substring (FTS). Beweist Index-AND-FTS-Kombination.
    /// </summary>
    [Fact]
    public async Task LogsSeite_LogQl_FeldregexMitBodyVolltext()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var nowNs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L - 60_000_000_000L;
        sink.WriteLogs(new[]
        {
            new HLogRecord(nowNs, HSeverity.Error, "ERROR", "db timeout in query", null, null,
                new[] { new HAttribute("http.route", "/api/orders") },
                new HResource(new[] { new HAttribute("service.name", "shop") }), null),
            new HLogRecord(nowNs + 1, HSeverity.Info, "INFO", "order placed", null, null,
                new[] { new HAttribute("http.route", "/api/orders") },
                new HResource(new[] { new HAttribute("service.name", "shop") }), null),
        });

        var body = await (await Client.GetAsync("/otel/logs?q=" + System.Uri.EscapeDataString("{http.route=~\"^/api/.*\"} |= \"timeout\""))).Content.ReadAsStringAsync();
        Assert.Contains("db timeout in query", body);
        Assert.DoesNotContain("order placed", body);
    }

    // === Service-/Version-Dropdowns (Logs + Traces) =======================

    /// <summary>
    /// Seed-Helfer: Logs + Spans mit service.name (+ optional service.version).
    /// Zeitstempel 60s in der Vergangenheit (NowUnixNano() trunkiert auf
    /// Sekunden — sicher im 1h-Default-Fenster, Muster der Tests oben).
    /// </summary>
    private static long SeedNowNs() =>
        System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L - 60_000_000_000L;

    private static HLogRecord SeedLog(long t, string body, string svc, string? ver = null) =>
        new(t, HSeverity.Info, "INFO", body, null, null, System.Array.Empty<HAttribute>(),
            ver is null
                ? new HResource(new[] { new HAttribute("service.name", svc) })
                : new HResource(new[] { new HAttribute("service.name", svc), new HAttribute("service.version", ver) }),
            null);

    private static HSpan SeedSpan(byte[] tid, byte[] sid, string svc, string? ver = null) =>
        new(tid, sid, null, "op", HSpanKind.Server,
            SeedNowNs(), SeedNowNs() + 1_000_000, HStatusCode.Ok, null,
            System.Array.Empty<HAttribute>(), System.Array.Empty<HSpanEvent>(), System.Array.Empty<HSpanLink>(),
            ver is null
                ? new HResource(new[] { new HAttribute("service.name", svc) })
                : new HResource(new[] { new HAttribute("service.name", svc), new HAttribute("service.version", ver) }),
            null);

    /// <summary>
    /// Die Service-Chips listen die Discovery-Menge aus Logs UND Spans —
    /// ein Service, der nur Traces schickt („traceonly"), taucht ebenfalls auf.
    /// Kein Haken gesetzt = „alle" (kein svc=-Param).
    /// </summary>
    [Fact]
    public async Task LogsSeite_ServiceChips_ListetServicesAusLogsUndSpans()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[] { SeedLog(t, "msg-shop", "shop") });
        sink.WriteSpans(new[] { SeedSpan(TraceId, RootSpanId, "traceonly") });

        var body = await (await Client.GetAsync("/otel/logs")).Content.ReadAsStringAsync();
        Assert.Contains("hmd-chip", body);                   // Chip-Gruppe statt Select
        Assert.DoesNotContain("<select name=\"svc\"", body); // altes Dropdown ist weg
        Assert.Contains("name=\"svc\"", body);
        Assert.Contains("value=\"shop\"", body);             // Log-seitiger Discovery-Zweig
        Assert.Contains("value=\"traceonly\"", body);        // Span-seitiger Discovery-Zweig
    }

    /// <summary>
    /// Das Version-Dropdown ist abhängig vom Service: ohne svc disabled mit
    /// Platzhalter, mit svc=shop nur die Versionen von shop (v1/v2 — nicht
    /// die billing-Version v9).
    /// </summary>
    [Fact]
    public async Task LogsSeite_VersionDropdown_AbhaengigVomService()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[]
        {
            SeedLog(t,     "msg-shop-v1", "shop",    "v1"),
            SeedLog(t + 1, "msg-shop-v2", "shop",    "v2"),
            SeedLog(t + 2, "msg-bill-v9", "billing", "v9"),
        });

        var ohneSvc = await (await Client.GetAsync("/otel/logs")).Content.ReadAsStringAsync();
        Assert.Contains("<select name=\"ver\" disabled", ohneSvc);   // kein Service -> keine Version

        var mitSvc = await (await Client.GetAsync("/otel/logs?svc=shop")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"v1\"", mitSvc);
        Assert.Contains("value=\"v2\"", mitSvc);
        Assert.DoesNotContain("value=\"v9\"", mitSvc);               // nur Versionen DES Service
    }

    /// <summary>
    /// Filter-Wirkung: <c>?svc=shop&ver=v2</c> zeigt nur das shop/v2-Log;
    /// <c>?svc=shop</c> alle shop-Logs. UND mit dem LogQL-Feldfilter bleibt
    /// erhalten (Konflikt = leere Menge, keine Merge-Magie).
    /// </summary>
    [Fact]
    public async Task LogsSeite_FiltertAufServiceUndVersion()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[]
        {
            SeedLog(t,     "msg-shop-v1", "shop",    "v1"),
            SeedLog(t + 1, "msg-shop-v2", "shop",    "v2"),
            SeedLog(t + 2, "msg-bill-v1", "billing", "v1"),
        });

        var pair = await (await Client.GetAsync("/otel/logs?svc=shop&ver=v2")).Content.ReadAsStringAsync();
        Assert.Contains("msg-shop-v2", pair);
        Assert.DoesNotContain("msg-shop-v1", pair);
        Assert.DoesNotContain("msg-bill-v1", pair);   // gleiches v1, aber anderer Service

        var svc = await (await Client.GetAsync("/otel/logs?svc=shop")).Content.ReadAsStringAsync();
        Assert.Contains("msg-shop-v1", svc);
        Assert.Contains("msg-shop-v2", svc);
        Assert.DoesNotContain("msg-bill-v1", svc);
    }

    /// <summary>
    /// Sanitierung: eine Version, die es beim gewählten Service nicht gibt
    /// (stale DOM: Service gewechselt, altes ver mit submittet; oder alter
    /// Bookmark), wird auf „alle" zurückgesetzt — die Seite bleibt 200 und
    /// zeigt die Service-Logs ohne Versions-Filter.
    /// </summary>
    [Fact]
    public async Task LogsSeite_StaleVersionWirdSanitiert()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[]
        {
            SeedLog(t,     "msg-shop-v1", "shop",    "v1"),
            SeedLog(t + 1, "msg-bill-v2", "billing", "v2"),
        });

        var resp = await Client.GetAsync("/otel/logs?svc=shop&ver=v2");   // v2 nur bei billing
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("msg-shop-v1", body);          // Versions-Filter fiel weg, Service blieb
    }

    /// <summary>
    /// Traces-Seite: Service-Dropdown filtert exakt (kein Substring mehr) —
    /// der Billing-Trace verschwindet, der Shop-Trace bleibt.
    /// </summary>
    [Fact]
    public async Task TracesSeite_ServiceDropdownFiltert()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var billingTid = new byte[] { 0xb2, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        sink.WriteSpans(new[]
        {
            SeedSpan(TraceId, RootSpanId, "shop"),
            SeedSpan(billingTid, RootSpanId, "billing"),
        });

        var body = await (await Client.GetAsync("/otel/traces?svc=billing")).Content.ReadAsStringAsync();
        Assert.Contains("b201020304050607", body);      // Billing-Trace (Href + Truncate)
        Assert.DoesNotContain("b101020304050607", body); // Shop-Trace gefiltert

        // Unbekannter/Teil-Service ("bil") wird von der Sanitierung auf „alle"
        // zurueckgesetzt (Bookmarks/Tippfehler) — alle Traces sichtbar. Die
        // exakt-statt-Substring-Semantik beweist der Storage-Test
        // (ListTraces_ServiceName_ExaktStattSubstring).
        var exakt = await (await Client.GetAsync("/otel/traces?svc=bil")).Content.ReadAsStringAsync();
        Assert.Contains("b201020304050607", exakt);
        Assert.Contains("b101020304050607", exakt);
    }

    /// <summary>
    /// Multi-Select: <c>?svc=shop&amp;svc=billing</c> zeigt die Logs BEIDER
    /// Services, nicht die des dritten (worker). Der Sort-Link (URL-Builder)
    /// erhält beide wiederholten svc=-Params.
    /// </summary>
    [Fact]
    public async Task LogsSeite_MultiServiceFilter_FiltertAufBeide()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[]
        {
            SeedLog(t,     "msg-shop",   "shop"),
            SeedLog(t + 1, "msg-bill",   "billing"),
            SeedLog(t + 2, "msg-worker", "worker"),
        });

        var body = await (await Client.GetAsync("/otel/logs?svc=shop&svc=billing")).Content.ReadAsStringAsync();
        Assert.Contains("msg-shop", body);
        Assert.Contains("msg-bill", body);
        Assert.DoesNotContain("msg-worker", body);
        // Pager/Sort-URLs transportieren die Mehrfachauswahl (href hat &amp;).
        Assert.Contains("svc=shop&amp;svc=billing", body);
    }

    /// <summary>
    /// Version-Dropdown bei Mehrfachauswahl: Version ist Paar-Semantik zu
    /// GENAU EINEM Service — bei zwei gewählten Services ist das Select
    /// disabled; ein trotzdem mitgesendetet ver wird ignoriert (kein Filter).
    /// </summary>
    [Fact]
    public async Task LogsSeite_MultiServiceFilter_VersionDisabled()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[]
        {
            SeedLog(t,     "msg-shop",   "shop",    "v1"),
            SeedLog(t + 1, "msg-bill",   "billing", "v2"),
        });

        var body = await (await Client.GetAsync("/otel/logs?svc=shop&svc=billing&ver=v2")).Content.ReadAsStringAsync();
        Assert.Contains("<select name=\"ver\" disabled", body);
        // ver fiel weg (kein Paar zu 2 Services): beide Logs bleiben sichtbar.
        Assert.Contains("msg-shop", body);
        Assert.Contains("msg-bill", body);
    }

    /// <summary>
    /// Traces-Seite Multi-Select: <c>?svc=shop&amp;svc=billing</c> zeigt die
    /// Traces beider Services, nicht die des dritten (worker).
    /// </summary>
    [Fact]
    public async Task TracesSeite_MultiServiceFilter_FiltertAufBeide()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var billingTid = new byte[] { 0xb2, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var workerTid = new byte[] { 0xb3, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        sink.WriteSpans(new[]
        {
            SeedSpan(TraceId, RootSpanId, "shop"),
            SeedSpan(billingTid, RootSpanId, "billing"),
            SeedSpan(workerTid, RootSpanId, "worker"),
        });

        var body = await (await Client.GetAsync("/otel/traces?svc=shop&svc=billing")).Content.ReadAsStringAsync();
        Assert.Contains("b101020304050607", body);      // Shop-Trace
        Assert.Contains("b201020304050607", body);      // Billing-Trace
        Assert.DoesNotContain("b301020304050607", body); // Worker-Trace gefiltert
    }

    // === Signal-Band / Wachtband (Übersicht) =============================

    /// <summary>
    /// Das Wachtband-Hero auf der Übersicht: mit Daten rendert es drei Lanes
    /// (Spans/Logs/Metrik-Punkte) über dem festen 1h-Fenster — jede Lane mit
    /// Meta-Block (Name, letzter Bucket, ∅/s), SVG (Linie/Fläche/Endpunkt-Dot)
    /// und Hover-Payload (data-vals) sowie Achsen-Text. Die Lane-Werte sind
    /// ohne JS vollständig; die Achse ist reiner Text.
    /// </summary>
    [Fact]
    public async Task Uebersicht_Wachtband_RendertDreiLanes()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();                          // sicher im festen 1h-Fenster
        sink.WriteSpans(new[] { SeedSpan(TraceId, RootSpanId, "shop") });
        sink.WriteLogs(new[] { SeedLog(t, "msg-shop", "shop") });
        sink.WriteMetrics(new[] { MetricPoint("orders", t, 1) });

        var resp = await Client.GetAsync("/otel");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("hmd-band", body);                            // Band-Section
        Assert.Equal(3, CountOccurrences(body, "hmd-band-lane--"));    // Spans/Logs/Metriken
        Assert.Contains("hmd-band-lane--spans", body);
        Assert.Contains("hmd-band-lane--logs", body);
        Assert.Contains("hmd-band-lane--metrics", body);
        Assert.Equal(3, CountOccurrences(body, "hmd-band-enddot"));   // Endpunkt-Dot je Lane
        Assert.Equal(3, CountOccurrences(body, "data-vals="));        // Hover-Payload je Lane
        Assert.Equal(3, CountOccurrences(body, "data-max=\"1\""));    // Null-Basis: max >= 1

        var decoded = System.Net.WebUtility.HtmlDecode(body);
        Assert.Contains("Signale · letzte Stunde", decoded);          // Band-Titel
        Assert.Contains("vor 60 min", decoded);                        // Achse links
        Assert.Contains("jetzt", decoded);                            // Achse rechts
    }

    /// <summary>
    /// Ohne Daten bleibt es beim geführten Empty-State — das Band (und seine
    /// drei Null-Lanes) taucht nicht auf; das Onboarding steht allein.
    /// </summary>
    [Fact]
    public async Task Uebersicht_LeereDB_KeinBand()
    {
        var body = await (await Client.GetAsync("/otel")).Content.ReadAsStringAsync();
        Assert.Contains("hmd-empty-state", body);
        Assert.DoesNotContain("hmd-band", body);
        Assert.DoesNotContain("hmd-band-lane", body);
    }

    private static HMetricPoint MetricPoint(string name, long tNs, double value) =>
        new(name, "1", HMetricType.Gauge, HTemporality.Unspecified, tNs, value,
            null, null, null, null, null, null,
            System.Array.Empty<HAttribute>(), null, null);

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
#endif