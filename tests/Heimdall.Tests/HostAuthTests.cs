#if NET10_0
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Heimdall;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// C2 — Auth-Review am Stand-alone-Host: API-Key (Header <c>x-heimdall-key</c>) für
/// OTLP/HTTP- + Prom-Pfade, Basic-Auth für die UI. Header only — kein Query-Fallback
/// ( <c>?key=</c> ohne Header → 401, da Query-Strings in Access-Logs landen). Vergleiche
/// zeitkonstant (<see cref="SecretComparer"/>).
/// </summary>
public class HostAuthTests : HostBootTestBase
{
    private const string ApiKey = "test-api-key-123";
    private const string Password = "test-ui-pw-456";

    public HostAuthTests()
    {
        // Basis setzt Auth.Enabled=false — hier überschreiben (vor lazily Host-Boot).
        SetEnv("Heimdall__Auth__Enabled", "true");
        SetEnv("Heimdall__Auth__ApiKey", ApiKey);
        SetEnv("Heimdall__Auth__Password", Password);
    }

    private ByteArrayContent TraceContent()
    {
        var c = new ByteArrayContent(BuildTraceRequest("auth-test-span").ToByteArray());
        c.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return c;
    }

    [Fact]
    public async Task Otlp_Ohne_ApiKey_Header_Liefert_401()
    {
        var resp = await Client.PostAsync("/otel/v1/traces", TraceContent());
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Otlp_Mit_Korrektem_ApiKey_Header_Liefert_200()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/otel/v1/traces") { Content = TraceContent() };
        msg.Headers.Add("x-heimdall-key", ApiKey);
        var resp = await Client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, Query.CountSpans());
    }

    [Fact]
    public async Task Otlp_Query_Key_Ohne_Header_Liefert_401()
    {
        // Query-Fallback wurde entfernt (C2) — ?key= ohne Header darf nicht durchgehen.
        var resp = await Client.PostAsync("/otel/v1/traces?key=" + WebUtility.UrlEncode(ApiKey), TraceContent());
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Otlp_Mit_Falschem_ApiKey_Liefert_401()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/otel/v1/traces") { Content = TraceContent() };
        msg.Headers.Add("x-heimdall-key", "wrong-key");
        var resp = await Client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Ui_Ohne_BasicAuth_Liefert_Redirect_Auf_Login()
    {
        // Login-Seite: UI ohne Creds redirectet auf /login (statt 401+Basic-Challenge).
        // ClientNoRedirect, sonst folgt der Client dem 302 zur Login-Seite (200).
        var resp = await ClientNoRedirect.GetAsync("/otel");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/login", resp.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task Ui_Mit_Korrektem_BasicAuth_Liefert_200()
    {
        var basic = System.Convert.ToBase64String(Encoding.UTF8.GetBytes("anyuser:" + Password));
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var resp = await Client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ui_Mit_Korrektem_UsernameUndPasswort_Liefert_200()
    {
        // Username konfiguriert → muss zusätzlich zum Passwort passen.
        SetEnv("Heimdall__Auth__Username", "admin");
        var basic = System.Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + Password));
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var resp = await Client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ui_Mit_Falschem_Username_Liefert_Redirect_Auf_Login()
    {
        // Username konfiguriert, aber Request schickt einen anderen → Redirect auf Login.
        SetEnv("Heimdall__Auth__Username", "admin");
        var basic = System.Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong:" + Password));
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var resp = await ClientNoRedirect.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
    }
}
#endif