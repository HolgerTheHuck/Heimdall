#if NET10_0
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// PathBase-Deployment-Tests (IIS-Unterverzeichnis / Reverse-Proxy mit Pfad-Strip):
/// die Clients fahren mit BaseAddress <c>http://localhost/otel/</c> — der TestServer
/// setzt diesen Basis-Pfad als <c>Request.PathBase</c>, exakt wie das ASP.NET Core
/// Module bei einer IIS-Sub-Application. Verifiziert, dass alle generierten URLs
/// (Root-Redirect, Nav-Links, Assets, Auth-Redirect) das Deployment-Verzeichnis
/// mitschleppen statt am Domain-Root zu landen. Site-Root-Verhalten (PathBase leer)
/// bleibt ungetestet identisch — <see cref="HostCompositionTests"/>.
/// </summary>
public class HostPathBaseTests : HostBootTestBase
{
    private readonly HttpClient _client;

    public HostPathBaseTests()
    {
        // Server-seitig: alle Requests bekommen PathBase "/otel" (wie ANCM-Sub-App);
        // Client-seitig: relative Pfade lösen gegen das externe Verzeichnis auf.
        UsePathBase("/otel");
        _client = CreateClient(new Uri("http://localhost/otel/"));
    }

    [Fact]
    public async Task Healthz_Unter_PathBase_Liefert_200()
    {
        var resp = await _client.GetAsync("healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Root_Redirectet_Auf_Externen_Prefix()
    {
        var resp = await CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false).GetAsync("");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/otel/otel", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Dashboard_Unter_Externem_Prefix_Liefert_200_und_vollstaendige_Links()
    {
        var resp = await _client.GetAsync("otel");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        // Nav-Links tragen PathBase + Dashboard-Prefix (extern /otel/otel/...).
        Assert.Contains("href=\"/otel/otel/dashboard\"", html);
        // Assets liegen am App-Root — nur PathBase, ohne doppelten Prefix.
        Assert.Contains("/otel/_content/Heimdall.Blazor/css/heimdall.css", html);
        Assert.DoesNotContain("href=\"/_content/Heimdall.Blazor/css/heimdall.css\"", html);
    }

    /// <summary>Regression: die Redirects des Dashboard-Import-POSTs müssen die
    /// PathBase mitführen (wie alle anderen POST-Redirects auch) — sonst 404 nach
    /// JEDEM Submit unter IIS-Unterverzeichnis/Proxy-Pfad-Strip.</summary>
    [Fact]
    public async Task Dashboard_Import_Post_Redirectet_Mit_PathBase()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(
            @"{ ""uid"": ""pb-import-test"", ""title"": ""PathBase Import"", ""panels"": [] }"),
            "file", "dashboard.json");

        var resp = await CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false)
            .PostAsync("otel/dashboards/import", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/otel/otel/dashboards/pb-import-test", resp.Headers.Location?.ToString());
    }

    /// <summary>Regression: der Leer-Submit-Fehler-Redirect (err=QueryParam) muss
    /// ebenfalls die PathBase tragen, sonst verliert die Fehlermeldung den Kontext.</summary>
    [Fact]
    public async Task Dashboard_Import_Post_Ohne_Inhalt_Redirectet_Mit_PathBase()
    {
        // Wie das echte Formular ohne Auswahl: leerer file-Teil + leeres json-Feld.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(string.Empty), "file", "empty.json");
        form.Add(new StringContent(string.Empty), "json");

        var resp = await CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false)
            .PostAsync("otel/dashboards/import", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? string.Empty;
        Assert.StartsWith("/otel/otel/dashboards/import?err=", loc);
    }
}

/// <summary>
/// Auth-Redirect unter PathBase: der 302 auf die Login-Seite (und der returnUrl)
/// müssen das Deployment-Verzeichnis tragen, sonst landet der Browser am Site-Root.
/// </summary>
public class HostPathBaseAuthTests : HostBootTestBase
{
    private const string ApiKey = "pb-api-key-1";
    private const string Password = "pb-ui-pw-2";

    public HostPathBaseAuthTests()
    {
        // Reihenfolge zwingend: Env-Vars VOR UsePathBase (das greift auf den Server
        // zu und bootet den Host lazily — danach gesetzte Auth-Env kämen zu spät).
        SetEnv("Heimdall__Auth__Enabled", "true");
        SetEnv("Heimdall__Auth__ApiKey", ApiKey);
        SetEnv("Heimdall__Auth__Password", Password);
        UsePathBase("/otel");
    }

    [Fact]
    public async Task Ui_Ohne_Cookie_Redirectet_Auf_Login_Mit_PathBase()
    {
        // extern /otel/traces → intern Path=/traces, PathBase=/otel.
        var resp = await CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false)
            .GetAsync("traces");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? string.Empty;
        Assert.StartsWith("/otel/otel/login?returnUrl=", loc);
        Assert.Contains("returnUrl=%2Fotel%2Ftraces", loc);
    }
}
#endif