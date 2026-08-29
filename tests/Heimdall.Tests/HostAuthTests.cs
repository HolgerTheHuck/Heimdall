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

/// <summary>
/// C2 — kompletter Browser-Login-Flow am Stand-alone-Host: Login-Seite rendern,
/// POST mit falschen/rechten Credentials (Form-Body, exakt wie das HTML-Formular),
/// Session-Cookie mitführen, geschützte Seite erreichen, Logout löscht die Session.
/// Einziger Test, der den <c>/otel/login</c>-Formularpfad (POST → 302 → Cookie → 200) abdeckt.
/// </summary>
public class HostLoginFlowTests : HostBootTestBase
{
    private const string Username = "admin";
    private const string Password = "login-test-pw-789";

    public HostLoginFlowTests()
    {
        SetEnv("Heimdall__Auth__Enabled", "true");
        SetEnv("Heimdall__Auth__Username", Username);
        SetEnv("Heimdall__Auth__Password", Password);
        SetEnv("Heimdall__Auth__ApiKey", "login-test-api-key");
    }

    private static FormUrlEncodedContent LoginContent(string user, string pw, string? returnUrl = null) =>
        new(new Dictionary<string, string>
        {
            ["username"] = user,
            ["password"] = pw,
            ["returnUrl"] = returnUrl ?? "/otel",
        });

    private static string? SessionCookie(HttpResponseMessage resp)
    {
        // Set-Cookie: "heimdall-auth=<wert>; HttpOnly; …" → Name=Wert vor dem ersten ';'.
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        foreach (var v in values)
            if (v.StartsWith("heimdall-auth=") && v["heimdall-auth=".Length] != ';')
                return v.Split(';')[0];
        return null;
    }

    [Fact]
    public async Task Login_Seite_Liefert_Formular()
    {
        var resp = await Client.GetAsync("/otel/login");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        Assert.Contains("<form", body);
        Assert.Contains("name=\"username\"", body);
        Assert.Contains("name=\"password\"", body);
    }

    [Fact]
    public async Task Login_Mit_Falschem_Passwort_Redirected_Mit_Fehler()
    {
        var resp = await ClientNoRedirect.PostAsync("/otel/login",
            LoginContent(Username, "wrong-password"));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? "";
        Assert.Contains("/otel/login", loc);
        Assert.Contains("err=", loc);
    }

    [Fact]
    public async Task Login_Mit_Korrekten_Credentials_Setzt_Cookie_Und_Erreicht_Geschuetzte_Seite()
    {
        var login = await ClientNoRedirect.PostAsync("/otel/login", LoginContent(Username, Password));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var cookie = SessionCookie(login);
        Assert.False(string.IsNullOrEmpty(cookie));

        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel/traces");
        msg.Headers.TryAddWithoutValidation("Cookie", cookie);
        var resp = await Client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Ohne Cookie bleibt dieselbe Seite geschützt (302 auf Login).
        var ohne = await ClientNoRedirect.GetAsync("/otel/traces");
        Assert.Equal(HttpStatusCode.Redirect, ohne.StatusCode);
        Assert.Contains("/login", ohne.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task Logout_Loescht_Session_Und_Redirected_Auf_Login()
    {
        var login = await ClientNoRedirect.PostAsync("/otel/login", LoginContent(Username, Password));
        var cookie = SessionCookie(login);
        Assert.False(string.IsNullOrEmpty(cookie));

        var logout = await ClientNoRedirect.PostAsync("/otel/logout", new StringContent(""));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains("/login", logout.Headers.Location?.ToString() ?? "");
        // Session-Cookie wird im Browser gelöscht (Max-Age=0) — Sessions sind
        // zustandslos (HMAC+Expiry), serverseitig gibt es nichts zu invalidieren.
        Assert.Contains("heimdall-auth=", logout.Headers.ToString());
    }

    [Fact]
    public async Task Login_Post_Mit_Origin_Ohne_Port_Wird_Akzeptiert()
    {
        // Browser lassen Default-Ports (80/443) im Origin weg; der Host-Header trägt
        // ihn ebenfalls nicht (HostString.Port == null). Der frühere rohe Port-Vergleich
        // (80 vs. null) fälschte das in "cross-origin POST rejected" um — Regression für
        // Login unter IIS auf Default-Port.
        var client = CreateClient(new Uri("http://server/"), allowAutoRedirect: false);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/otel/login")
        { Content = LoginContent(Username, Password) };
        msg.Headers.Add("Origin", "http://server");
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.False(string.IsNullOrEmpty(SessionCookie(resp)));
    }

    [Fact]
    public async Task Login_Post_Mit_Https_Origin_Gegen_Http_Backend_Wird_Akzeptiert()
    {
        // TLS-terminierender Proxy ohne Forwarded-Header: Browser-Origin https,
        // Backend http — beide Ports sind Schema-Defaults → Same-Origin.
        var client = CreateClient(new Uri("http://server/"), allowAutoRedirect: false);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/otel/login")
        { Content = LoginContent(Username, Password) };
        msg.Headers.Add("Origin", "https://server");
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.False(string.IsNullOrEmpty(SessionCookie(resp)));
    }

    [Fact]
    public async Task Login_Post_Hinter_Proxy_Mit_Forwarded_Headers_Wird_Akzeptiert()
    {
        // IIS-ARR/Reverse-Proxy: X-Forwarded-Host/-Proto beschreiben die externe
        // Authority, gegen die der Origin verglichen wird.
        var client = CreateClient(new Uri("http://localhost/"), allowAutoRedirect: false);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/otel/login")
        { Content = LoginContent(Username, Password) };
        msg.Headers.Add("Origin", "https://server");
        msg.Headers.Add("X-Forwarded-Host", "server");
        msg.Headers.Add("X-Forwarded-Proto", "https");
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.False(string.IsNullOrEmpty(SessionCookie(resp)));
    }

    [Fact]
    public async Task Login_Post_Mit_Fremdem_Origin_Weiterhin_Zurueckgewiesen()
    {
        // Der Fix darf den CSRF-Schutz nicht aufweichen: fremder Host → 400.
        var client = CreateClient(new Uri("http://server/"), allowAutoRedirect: false);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/otel/login")
        { Content = LoginContent(Username, Password) };
        msg.Headers.Add("Origin", "http://evil.example");
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("cross-origin", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_Stylesheet_Wird_Nicht_Hinter_Auth_Gestellt()
    {
        // Die Login-Seite lädt ihr Stylesheet (/_content/Heimdall.Blazor/css/…)
        // gerade OHNE Session-Cookie — stünde es hinter der Auth (302 auf den
        // Login-Redirect statt 200 text/css), renderte der Login-Screen unstyled.
        // Das AnonymousPrefixes-Array (Host trägt /_content/Heimdall.Blazor/ ein)
        // muss den Request an der Middleware vorbei reichen. Das Test-Factory-
        // Environment liefert Static-Web-Assets evtl. nicht (404 statt 200) —
        // die Auth-Regression äußert sich aber stets als Redirect.
        var resp = await ClientNoRedirect.GetAsync("/_content/Heimdall.Blazor/css/heimdall.css");
        Assert.NotEqual(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Prefix_Umgeht_Nur_Exakten_Heimdall_Asset_Prefix()
    {
        // Der Prefix-Match ist exakt (inkl. Slash): /_content/Heimdall.BlazorX/
        // fällt NICHT unter die Ausnahme und bleibt geschützt (Redirect zum Login).
        var resp = await ClientNoRedirect.GetAsync("/_content/Heimdall.BlazorX/css/heimdall.css");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/login", resp.Headers.Location?.ToString() ?? "");
    }
}
#endif