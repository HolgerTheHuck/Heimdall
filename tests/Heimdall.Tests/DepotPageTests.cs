#if NET10_0
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Heimdall;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// UI-Test der Depot-Seite (/depot): Bootet den Host, sendet Telemetrie über den
/// registrierten Sink und verifiziert, dass die Seite 200 liefert und die
/// Tabellenaufstellung + KPIs rendert. ASCII-Assertions (Razor kodiert Umlaute —
/// HtmlDecode nur wo nötig, siehe HeimdallUiTests).
/// </summary>
public class DepotPageTests : HostBootTestBase
{
    [Fact]
    public async Task Depot_Seite_Rendert_Tabellen_und_KPIs()
    {
        // Leere DB → Seite muss trotzdem 200 liefern (Operator-Sicht ohne Daten).
        var resp = await Client.GetAsync("/otel/depot");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());

        Assert.Contains("Depot", body);
        Assert.Contains("heim_spans", body);
        Assert.Contains("heim_logs", body);
        Assert.Contains("hmd-kpi-row", body);

        // Konfig-Hinweis-Panel: Retention-Zeile sichtbar (Host-Defaults aus appsettings:
        // Traces 3 / Logs 14 / Metriken 30 — wir pinnen nur das ASCII-Label).
        Assert.Contains("Retention", body);
    }

    [Fact]
    public async Task Depot_Seite_Nach_Writes_Liefert_Weiterhin_200()
    {
        // Log schreiben (Seeding-Pattern der Host-Tests) — die Depot-Seite muss
        // mit Daten weiterhin 200 liefern; die Zeilenzahl prüfen wir am Query
        // (der Body selbst wird auf der Depot-Seite nicht gerendert).
        var sink = (IHeimdallSink)Services.GetRequiredService(typeof(IHeimdallSink));
        sink.WriteLogs(new[]
        {
            new HLogRecord(HeimdallTestClock.NowNs(), HSeverity.Info, "INFO", "depot-page-test",
                null, null, System.Array.Empty<HAttribute>(), null, null),
        });
        Assert.Equal(1, Query.CountLogs());

        var resp = await Client.GetAsync("/otel/depot");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        Assert.Contains("heim_logs", body);
        Assert.Contains("hmd-kpi-row", body);
        // Format v2: nach dem Write ist die Kompressions-Buchhaltung gefüllt →
        // Brotli-Hinweis-Zeile sichtbar.
        Assert.Contains("Brotli", body);
    }
}

/// <summary>Gemeinsame Zeit-Helfer der Host-Tests (Unix-Ns).</summary>
internal static class HeimdallTestClock
{
    private static readonly long EpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, System.TimeSpan.Zero).UtcTicks;
    public static long NowNs() => (System.DateTimeOffset.UtcNow.UtcTicks - EpochTicks) * 100L;
}
#endif