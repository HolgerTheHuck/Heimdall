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
    [InlineData("/otel/traces?limit=&offset=&from=&to=&preset=24h")]
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