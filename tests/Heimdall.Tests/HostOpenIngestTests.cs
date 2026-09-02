#if NET10_0
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Google.Protobuf;
using Heimdall;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Offener Ingest (ApiKey leer konfiguriert): Auth.Enabled=true mit
/// Username/Password schützt die UI per Login, während die API-Pfade
/// (OTLP/HTTP, Prom-API) OHNE <c>x-heimdall-key</c> durchgelassen werden —
/// für Sender im geschützten Netz, die keinen Key mitsenden. Regression gegen
/// den alten „sicherer Default“ (fehlender ApiKey → 401 auf ALLE API-POSTs).
/// </summary>
public class HostOpenIngestTests : HostBootTestBase
{
    private const string Password = "open-ingest-pw-1";

    public HostOpenIngestTests()
    {
        // Auth an, Username/Password gesetzt — ApiKey bewusst NICHT gesetzt
        // (leer = offener Ingest). Wichtig: VOR dem lazily Host-Boot.
        SetEnv("Heimdall__Auth__Enabled", "true");
        SetEnv("Heimdall__Auth__Username", "admin");
        SetEnv("Heimdall__Auth__Password", Password);
        SetEnv("Heimdall__Auth__ApiKey", null);
    }

    private ByteArrayContent TraceContent()
    {
        var c = new ByteArrayContent(BuildTraceRequest("open-ingest-span").ToByteArray());
        c.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return c;
    }

    [Fact]
    public async Task Otlp_Ohne_Key_Landet_Im_Sink()
    {
        var resp = await Client.PostAsync("/otel/v1/traces", TraceContent());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, Query.CountSpans());
    }

    [Fact]
    public async Task PromApi_Ohne_Key_Liefert_200()
    {
        var resp = await Client.GetAsync("/otel/api/v1/query?query=up");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ui_Bleibt_Login_Geschuetzt()
    {
        var resp = await ClientNoRedirect.GetAsync("/otel");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/login", resp.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task Ui_Mit_UsernameUndPasswort_Liefert_200()
    {
        var basic = System.Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("admin:" + Password));
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var resp = await Client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
#endif