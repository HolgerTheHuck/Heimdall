#if NET10_0
using System;
using System.IO;
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
        // Logs ist eigener Top-Level-Tab (Hauptweg der Fehleranalyse); auf /logs
        // ist dieser aktiv, nicht der Drilldown-Tab.
        Assert.Contains("href=\"/otel/logs\" aria-current=\"page\"", body);
        Assert.DoesNotContain("href=\"/otel/drilldown\" aria-current=\"page\"", body);
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
        // Zeitstrahl-Spalte: 2 Spans → 2 Spuren + 2 Balken (Tooltip-Klasse bleibt).
        Assert.Equal(2, CountOccurrences(body, "hmd-tl-track"));
        Assert.Equal(2, CountOccurrences(body, "hmd-tl-bar"));
        Assert.Equal(2, CountOccurrences(body, "hmd-waterfall-bar"));   // Tooltip-Klasse erhalten
        // Histogramm: <details open>; Root (0ms) und Child (50ms von 500ms) landen in
        // verschiedenen Buckets → 2 belegte Buckets.
        Assert.Contains("<details class=\"hmd-tl-hist\" open>", body);
        Assert.Contains("hmd-tl-hist-chart", body);
        Assert.Equal(2, CountOccurrences(body, "hmd-tl-hist-bar"));
        // Kein SVG-Wasserfall mehr (Klassen des SVG-Baus verschwunden).
        Assert.DoesNotContain("hmd-waterfall-name", body);
        Assert.DoesNotContain("hmd-waterfall-dur", body);
        // DFS-Preorder: Root vor Child.
        Assert.True(body.IndexOf("GET /api/orders", System.StringComparison.Ordinal)
                  < body.IndexOf("db.query", System.StringComparison.Ordinal));
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
        // Presets sind Radios (nicht Submit-Buttons): der angehakte Wert wird bei
        // JEDEM Submit mitgeschickt, nicht nur wenn der Preset-Knopf selbst
        // abgeschickt hat. Aktiv-Zustand = checked + serverseitiges is-active.
        Assert.Contains("type=\"radio\" name=\"preset\" value=\"1h\" checked", body);
        Assert.Contains("hmd-range-btn is-active", body);
        Assert.Contains("name=\"refresh\"", body);        // Auto-Refresh-Select
        Assert.Contains("datetime-local", body);          // Benutzerdefiniert-Felder
    }

    /// <summary>
    /// Die Zeitauswahl ueberlebt einen Submit, der NICHT vom Preset-Knopf kam.
    /// Frueher trugen die Presets ihren Wert nur als Submit-Button-name/value —
    /// ein Klick auf „Suchen" (oder Enter im Suchfeld) schickte gar kein preset
    /// mit, und der Server fiel auf den Default zurueck. Als Radio wird der
    /// angehakte Wert immer mitgesendet; hier simuliert durch einen Request, der
    /// preset zusammen mit anderen Filtern traegt.
    /// </summary>
    [Fact]
    public async Task TimeRange_PresetUeberlebtFremdenSubmit()
    {
        // Wie der Browser es nach „Suchen" schickt: alle Formularfelder inkl. des
        // angehakten Preset-Radios.
        var body = await (await Client.GetAsync(
            "/otel/traces?name=&svc=&ver=&err=&limit=100&preset=24h&refresh=")).Content.ReadAsStringAsync();
        Assert.Contains("type=\"radio\" name=\"preset\" value=\"24h\" checked", body);
        Assert.DoesNotContain("type=\"radio\" name=\"preset\" value=\"1h\" checked", body);
    }

    /// <summary>
    /// Ohne <c>?preset=</c> markiert die Leiste das Preset, das der Server
    /// tatsaechlich anwendet (sein Fallback). Vorher war beim Landen auf einer
    /// Seite ohne Preset-Parameter NICHTS markiert, obwohl serverseitig ein
    /// Stundenfenster galt — die Leiste sah aus wie „alles deselektiert = alles
    /// sehen", zeigte aber nur die letzte Stunde.
    /// </summary>
    [Theory]
    [InlineData("/otel/logs", "1h")]
    [InlineData("/otel/traces", "1h")]
    [InlineData("/otel/endpoints", "24h")]   // eigene Seite, eigenes Fallback
    [InlineData("/otel/dashboard", "24h")]
    public async Task TimeRange_OhnePresetParam_MarkiertDasFallback(string url, string expected)
    {
        var body = await (await Client.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.Contains($"type=\"radio\" name=\"preset\" value=\"{expected}\" checked", body);
        Assert.Equal(1, CountOccurrences(body, "hmd-range-btn is-active"));
    }

    /// <summary>
    /// „Alles" ist der explizite Knopf fuer „alles sehen": er loest zu einem
    /// unbegrenzten Fenster auf und zeigt damit auch Daten, die aelter sind als
    /// jedes zeitliche Preset.
    /// </summary>
    [Fact]
    public async Task TimeRange_PresetAlles_ZeigtAuchAlteDaten()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var alt = SeedNowNs() - 3L * 24 * 3600 * 1_000_000_000L;   // 3 Tage alt
        sink.WriteLogs(new[] { SeedLog(alt, "uralt-eintrag", "shop") });

        // Default-Fenster (1 h) sieht ihn nicht ...
        var eng = await (await Client.GetAsync("/otel/logs")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("uralt-eintrag", eng);

        // ... „Alles" schon.
        var alles = await (await Client.GetAsync("/otel/logs?preset=all")).Content.ReadAsStringAsync();
        Assert.Contains("uralt-eintrag", alles);
        Assert.Contains("type=\"radio\" name=\"preset\" value=\"all\" checked", alles);
    }

    /// <summary>
    /// Kein Preset-Knopf ist mehr ein Submit-Button. Das schliesst die implizite
    /// Submission: Enter in einem Textfeld aktiviert den ERSTEN Submit-Button des
    /// Formulars — das war „15 Minuten", womit Suchbegriff+Enter still auf ein
    /// 15-Minuten-Fenster sprang.
    /// </summary>
    [Fact]
    public async Task TimeRange_PresetsSindKeineSubmitButtons()
    {
        var body = await (await Client.GetAsync("/otel/logs?preset=1h")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<button type=\"submit\" name=\"preset\"", body);
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
    /// Die Service-Combobox der Logs-Seite listet NUR Services mit Logs —
    /// ein Service, der nur Traces schickt („traceonly"), wird nicht angeboten
    /// (Auswahl würde leer laufen; die Traces-Seite bleibt bei der Union).
    /// Kein Haken gesetzt = „alle" (kein svc=-Param, kein Count-Badge).
    /// </summary>
    [Fact]
    public async Task LogsSeite_ServiceChips_NurServicesMitLogs()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[] { SeedLog(t, "msg-shop", "shop") });
        sink.WriteSpans(new[] { SeedSpan(TraceId, RootSpanId, "traceonly") });

        var body = await (await Client.GetAsync("/otel/logs")).Content.ReadAsStringAsync();
        Assert.Contains("hmd-msel", body);                        // Combobox statt Select
        Assert.Contains("hmd-msel-panel", body);                  // Panel mit Checkboxen gerendert
        Assert.DoesNotContain("<select name=\"svc\"", body);      // altes Dropdown ist weg
        Assert.DoesNotContain("hmd-msel-search", body);           // Canari: kein Suchfeld im Panel
        Assert.Contains("type=\"checkbox\" name=\"svc\"", body);
        Assert.Contains("value=\"shop\"", body);                  // Log-seitiger Service
        Assert.DoesNotContain("value=\"traceonly\"", body);       // Trace-only nicht angeboten
        Assert.Contains("data-hmd-msel-all", body);               // Summary „alle" via data-Attribut
        Assert.DoesNotContain("data-hmd-msel-count>1", body);      // kein Count-Badge ohne Auswahl

        // Traces-Seite bleibt bei der Union (Logs ∪ Spans).
        var traces = await (await Client.GetAsync("/otel/traces")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"traceonly\"", traces);
    }

    /// <summary>
    /// Geschlossenes Control der Combobox bei Auswahl: Summary zeigt die
    /// gewählten Namen, Count-Badge zählt, nur diese Boxen sind gecheckt —
    /// das Panel listet trotzdem ALLE Discovery-Services (nicht nur die
    /// Auswahl; Regressiv gegen versehentliches Rendern nur der Auswahl).
    /// </summary>
    [Fact]
    public async Task LogsSeite_MultiSelect_GeschlossenesControlMitZaehler()
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
        Assert.Contains("shop, billing", body);                   // Summary mit beiden Namen
        Assert.Contains("data-hmd-msel-count>2</span>", body);    // Count-Badge
        Assert.Contains("value=\"shop\" checked", body);
        Assert.Contains("value=\"billing\" checked", body);
        // Panel listet die Discovery-Menge vollständig, nicht nur die Auswahl.
        Assert.Contains("value=\"worker\"", body);
        Assert.DoesNotContain("value=\"worker\" checked", body);
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

    // === Quick Wins: Zaehlzeile, Limit-Klammer, Attribut-Filter, volle IDs ====

    /// <summary>
    /// Die Zaehlzeile („N auf dieser Seite · M gesamt") steht genau EINMAL im
    /// Dokument — auch in der gruppierten Ansicht (view=svc), wo sie frueher
    /// zusaetzlich am Ende des Gruppen-Zweigs gerendert wurde und damit doppelt
    /// untereinander stand.
    /// </summary>
    [Fact]
    public async Task LogsSeite_Zaehlzeile_GenauEinmal_AuchGruppiert()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[] { SeedLog(t, "msg-a", "shop"), SeedLog(t + 1, "msg-b", "billing") });

        var flat = await (await Client.GetAsync("/otel/logs")).Content.ReadAsStringAsync();
        Assert.Equal(1, CountOccurrences(flat, "class=\"hmd-count\""));

        var grouped = await (await Client.GetAsync("/otel/logs?view=svc")).Content.ReadAsStringAsync();
        Assert.Contains("hmd-log-group", grouped);              // Gruppen wirklich gerendert
        Assert.Equal(1, CountOccurrences(grouped, "class=\"hmd-count\""));
    }

    /// <summary>
    /// <c>?limit=</c> wird serverseitig geklemmt. Das <c>max</c>-Attribut am
    /// Zahlenfeld ist reine Client-Kosmetik — ueber die URL laesst sich sonst eine
    /// beliebig grosse Seite anfordern, die der Server materialisiert und rendert.
    /// Der geklemmte Wert schlaegt sowohl im Formularfeld als auch in den
    /// Pager-URLs durch.
    /// </summary>
    [Fact]
    public async Task LogsSeite_LimitWirdGeklemmt()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteLogs(new[] { SeedLog(SeedNowNs(), "msg", "shop") });

        var body = await (await Client.GetAsync("/otel/logs?limit=999999")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"limit\" value=\"5000\"", body);
        Assert.DoesNotContain("name=\"limit\" value=\"999999\"", body);
        // Die selbst gebauten URLs (Sortier-Links, Pager) tragen den geklemmten Wert —
        // der Beweis, dass auch die Abfrage geklemmt lief, nicht nur die Anzeige.
        Assert.Contains("limit=5000", body);
        Assert.DoesNotContain("limit=999999&", body);
    }

    /// <summary>Wie <see cref="LogsSeite_LimitWirdGeklemmt"/>, Traces-Seite (Cap 1000).</summary>
    [Fact]
    public async Task TracesSeite_LimitWirdGeklemmt()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteSpans(new[] { SeedSpan(TraceId, RootSpanId, "shop") });

        var body = await (await Client.GetAsync("/otel/traces?limit=999999")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"limit\" value=\"1000\"", body);
        Assert.DoesNotContain("name=\"limit\" value=\"999999\"", body);
        Assert.Contains("limit=1000", body);
        Assert.DoesNotContain("limit=999999&", body);
    }

    /// <summary>
    /// Attributwerte im aufgeklappten Log sind Links, die den Wert als
    /// LogQL-Stream-Selector auf die Logs-Seite uebernehmen — und dabei den
    /// aktiven Zeitraum mitnehmen statt auf den Default zurueckzufallen.
    /// </summary>
    [Fact]
    public async Task LogsSeite_AttributWert_IstFilterLinkMitZeitraum()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteLogs(new[]
        {
            new HLogRecord(SeedNowNs(), HSeverity.Info, "INFO", "checkout done", null, null,
                new[] { new HAttribute("http.route", "/checkout") },
                new HResource(new[] { new HAttribute("service.name", "shop") }), null),
        });

        var body = await (await Client.GetAsync("/otel/logs?preset=6h")).Content.ReadAsStringAsync();
        Assert.Contains("hmd-attr-filter", body);
        // q={http.route="/checkout"} — URL-kodiert im href.
        Assert.Contains("/logs?q=%7Bhttp.route%3D%22%2Fcheckout%22%7D", body);
        Assert.Contains("preset=6h", body);
    }

    /// <summary>
    /// Schluessel, die der LogQL-Stream-Selector nicht lesen kann (er akzeptiert nur
    /// <c>[A-Za-z0-9_.]</c>), bleiben reiner Text — ein Link wuerde still nach etwas
    /// anderem suchen als angezeigt.
    /// </summary>
    [Fact]
    public async Task LogsSeite_NichtAdressierbarerSchluessel_BleibtText()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteLogs(new[]
        {
            new HLogRecord(SeedNowNs(), HSeverity.Info, "INFO", "odd key", null, null,
                new[] { new HAttribute("weird-key[0]", "v") },
                new HResource(new[] { new HAttribute("service.name", "shop") }), null),
        });

        var body = await (await Client.GetAsync("/otel/logs")).Content.ReadAsStringAsync();
        Assert.Contains("weird-key[0]", body);        // Attribut selbst wird gezeigt
        Assert.DoesNotContain("hmd-attr-filter", body);
    }

    /// <summary>
    /// Gekuerzte TraceIds tragen den vollen Wert als <c>title</c> — sonst ist er
    /// nirgends erreichbar (Spalten kuerzen auf 12 bzw. 16 Zeichen).
    /// </summary>
    [Fact]
    public async Task GekuerzteTraceId_TraegtVollenWertAlsTitle()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteSpans(new[] { SeedSpan(TraceId, RootSpanId, "shop") });

        var hex = System.Convert.ToHexString(TraceId).ToLowerInvariant();
        Assert.Equal(32, hex.Length);                       // wirklich laenger als die Anzeige
        var body = await (await Client.GetAsync("/otel/traces")).Content.ReadAsStringAsync();
        Assert.Contains("title=\"" + hex + "\"", body);
    }

    // === Feedback-Runde: Checkbox-Abstand, Gruppen zuklappbar, Zurueck-Ziel ====

    /// <summary>
    /// Regression: <c>.hmd-filters input</c> bekam eine feste <c>width</c> (statt
    /// nur <c>min-width</c>), damit die Filterleiste bei Auswahl nicht mehr springt.
    /// Eine Checkbox matcht denselben Selektor, rendert aber nur ihren kleinen
    /// nativen Glyph — ohne Reset blieb ihre LAYOUT-Box 9rem breit, und der Abstand
    /// zum Label-Text mass vom Rand dieser unsichtbaren Box, nicht vom sichtbaren
    /// Haekchen (Box und Text standen sichtbar weit auseinander). Die Checkbox-
    /// Ausnahme muss <c>width</c> zuruecksetzen, nicht nur <c>min-width</c>.
    /// </summary>
    [Fact]
    public void Css_InlineCheckbox_SetztWidthZurueck_NichtNurMinWidth()
    {
        var css = File.ReadAllText(LocateRepoFile("src", "Heimdall.Blazor", "wwwroot", "css", "heimdall.css"));
        var idx = css.IndexOf(".hmd-inline-chk input", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Regel .hmd-inline-chk input fehlt");
        var rule = css.Substring(idx, css.IndexOf('}', idx) - idx);
        Assert.Contains("width: auto", rule);
    }

    /// <summary>
    /// Gruppierte Logs-Ansicht (view=svc): jede Service-Gruppe ist ein natives
    /// <c>&lt;details&gt;</c> (Default offen) — zuklappbar fuer den schnellen
    /// Ueberblick ueber die beteiligten Services, ohne dass die Zeilen verloren
    /// gehen (bleiben im DOM, nur eingeklappt).
    /// </summary>
    [Fact]
    public async Task LogsSeiteGruppiert_JedeGruppeIstZuklappbaresDetails()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteLogs(new[] { SeedLog(SeedNowNs(), "msg-a", "shop"), SeedLog(SeedNowNs() + 1, "msg-b", "billing") });

        var body = await (await Client.GetAsync("/otel/logs?view=svc&preset=1h")).Content.ReadAsStringAsync();
        Assert.Equal(2, CountOccurrences(body, "<details class=\"hmd-log-group\" data-group-key="));
        Assert.Contains("<details class=\"hmd-log-group\" data-group-key=\"shop\" open>", body);
        Assert.Contains("<details class=\"hmd-log-group\" data-group-key=\"billing\" open>", body);
        Assert.Contains("hmd-log-group-summary", body);
        // Kein <section>/<h2> mehr fuer die Gruppen-Ueberschrift.
        Assert.DoesNotContain("<h2 class=\"hmd-subhead\">", body);
    }

    /// <summary>
    /// Ein Trace-Link aus einer Log-Zeile traegt die aktuelle Seite (samt Filtern)
    /// als <c>ret=</c> — der Zurueck-Link auf dem Trace-Detail fuehrt dorthin
    /// zurueck, statt fest auf /traces. Kommt jemand direkt ueber die Traces-Seite
    /// (kein ret gesetzt), bleibt /traces das Ziel — unveraendertes Altverhalten.
    /// </summary>
    [Fact]
    public async Task LogsSeite_TraceLink_TraegtRetZuruecksZuLogs()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteSpans(new[] { SeedSpan(TraceId, RootSpanId, "shop") });
        sink.WriteLogs(new[]
        {
            new HLogRecord(SeedNowNs(), HSeverity.Info, "INFO", "log with trace", TraceId, RootSpanId,
                Array.Empty<HAttribute>(), new HResource(new[] { new HAttribute("service.name", "shop") }), null),
        });

        var logsUrl = "/otel/logs?preset=1h&sev=9";
        var logsBody = await (await Client.GetAsync(logsUrl)).Content.ReadAsStringAsync();
        var hex = System.Convert.ToHexString(TraceId).ToLowerInvariant();
        var expectedRet = "ret=" + Uri.EscapeDataString(logsUrl);
        Assert.Contains($"/otel/trace/{hex}?{expectedRet}", logsBody);

        // Der Trace-Detail-Link fuehrt tatsaechlich zur Logs-Seite mit denselben Filtern zurueck.
        var traceBody = await (await Client.GetAsync($"/otel/trace/{hex}?{expectedRet}")).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"{System.Net.WebUtility.HtmlEncode(logsUrl)}\"", traceBody);
    }

    /// <summary>
    /// Ein manipulierter <c>ret</c>, der nicht auf den eigenen BasePath zeigt
    /// (Open-Redirect-Versuch — fremde Domain oder fremder Pfad), wird verworfen;
    /// das Zurueck-Ziel faellt auf den alten Default <c>/traces</c> zurueck.
    /// Gleiche Regel wie beim Login-<c>returnUrl</c> und dem <c>/lang</c>-<c>ret</c>.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("/andere-app/geheim")]
    public async Task TraceDetail_FremdesRet_FaelltAufTracesZurueck(string evilRet)
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteSpans(new[] { SeedSpan(TraceId, RootSpanId, "shop") });
        var hex = System.Convert.ToHexString(TraceId).ToLowerInvariant();

        var body = await (await Client.GetAsync($"/otel/trace/{hex}?ret=" + Uri.EscapeDataString(evilRet))).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/otel/traces\"", body);
        Assert.DoesNotContain(evilRet, body);
    }

    // === Auto-Refresh soll Listen-Zustand nicht mehr zerstoeren =============

    /// <summary>
    /// Jede Log-Zeile traegt einen stabilen <c>data-log-key</c> — die Verankerung,
    /// die heimdall.js braucht, um eine manuell aufgeklappte Zeile nach einem
    /// Auto-Refresh-<c>location.reload()</c> wiederzufinden und erneut
    /// aufzuklappen (Reload rendert die Seite komplett neu, jeder rein
    /// clientseitige <c>&lt;details open&gt;</c>-Zustand geht sonst verloren).
    /// </summary>
    [Fact]
    public async Task LogsSeite_LogZeile_TraegtDataLogKey()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteLogs(new[] { SeedLog(SeedNowNs(), "msg", "shop") });

        var body = await (await Client.GetAsync("/otel/logs?preset=1h")).Content.ReadAsStringAsync();
        Assert.Contains("data-log-key=\"", body);
    }

    /// <summary>
    /// Jede Service-Gruppe (view=svc) traegt <c>data-group-key</c> = Servicename —
    /// dieselbe Verankerung fuer eine manuell ZUGEKLAPPTE Gruppe (Default ist
    /// offen), damit sie nach einem Auto-Refresh nicht wieder aufspringt.
    /// </summary>
    [Fact]
    public async Task LogsSeiteGruppiert_Gruppe_TraegtDataGroupKey()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        sink.WriteLogs(new[] { SeedLog(SeedNowNs(), "msg", "shop") });

        var body = await (await Client.GetAsync("/otel/logs?view=svc&preset=1h")).Content.ReadAsStringAsync();
        Assert.Contains("data-group-key=\"shop\"", body);
    }

    /// <summary>
    /// Zwei Log-Zeilen mit unterschiedlichem Body (aber sonst identischen
    /// Merkmalen) erhalten unterschiedliche Schluessel — sonst wuerde ein
    /// aufgeklappter Log nach dem Reload den FALSCHEN Nachbarn mit aufklappen.
    /// </summary>
    [Fact]
    public async Task LogsSeite_UnterschiedlicheBodies_ErgebenUnterschiedlicheLogKeys()
    {
        var sink = (IHeimdallSink)Services.GetService(typeof(IHeimdallSink))!;
        var t = SeedNowNs();
        sink.WriteLogs(new[] { SeedLog(t, "erste nachricht", "shop"), SeedLog(t, "zweite nachricht", "shop") });

        var body = await (await Client.GetAsync("/otel/logs?preset=1h")).Content.ReadAsStringAsync();
        var keys = new System.Collections.Generic.HashSet<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(body, "data-log-key=\"([^\"]*)\""))
            keys.Add(m.Groups[1].Value);
        Assert.Equal(2, keys.Count);
    }

    /// <summary>
    /// Regression: der JS-Handler fuer das Sichern/Wiederherstellen des Listen-
    /// Zustands ueber einen Auto-Refresh-Reload hinweg ist tatsaechlich
    /// ausgeliefert (Scroll-Position, aufgeklappte Logs/Gruppen, noch nicht
    /// abgeschickte Filter-Eingaben — sonst „zerstoert" jeder Reload die Liste,
    /// die Suche und offene Details).
    /// </summary>
    [Fact]
    public void HeimdallJs_EnthaeltAutoRefreshZustandserhalt()
    {
        // Ueber die statische Route gefetcht wäre das brüchig — die Test-Factory
        // liefert Static-Web-Assets nicht zuverlässig (siehe HostAuthTests-
        // Kommentar zu genau diesem Verhalten); direkt von der Platte gelesen.
        var js = File.ReadAllText(LocateRepoFile("src", "Heimdall.Blazor", "wwwroot", "js", "heimdall.js"));
        Assert.Contains("saveLogOpenState", js);
        Assert.Contains("restoreLogOpenState", js);
        Assert.Contains("saveFilterEdits", js);
        Assert.Contains("restoreFilterEdits", js);
    }

    private static string LocateRepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var d = dir; d is not null; d = Directory.GetParent(d)?.FullName)
        {
            var cand = Path.Combine(d, Path.Combine(parts));
            if (File.Exists(cand)) return cand;
        }
        return Path.Combine(parts);
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