#if NET10_0
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Regression: die Panel-Editor-POSTs (Erfolg UND Fehler-Redirect)
    /// muessen die PathBase mitfuehren wie die übrigen Dashboard-POSTs.</summary>
    [Fact]
    public async Task Dashboard_Panel_Post_Redirectet_Mit_PathBase()
    {
        var store = Services.GetRequiredService<Heimdall.Blazor.Grafana.IGrafanaDashboardStore>();
        store.Save("pb-panel-test", @"{ ""uid"": ""pb-panel-test"", ""title"": ""PathBase Panel"", ""panels"": [] }");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["panelKey"] = "", ["title"] = "Neu", ["type"] = "stat",
            ["gridX"] = "0", ["gridY"] = "0", ["gridW"] = "6", ["gridH"] = "4",
            ["tgtCount"] = "1", ["t0Expr"] = "up", ["thrCount"] = "0",
        });
        var resp = await CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false)
            .PostAsync("otel/dashboards/pb-panel-test/panel/save", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/otel/otel/dashboards/pb-panel-test", resp.Headers.Location?.ToString());
    }
}

/// <summary>
/// CSRF-Check mit TrustedOrigins (Reverse-Proxy mit abweichender TLD): der Browser
/// sendet beim Form-POST unausweisbar Origin/Referer mit der EXTERNEN Origin; reicht
/// der Proxy Host/X-Forwarded-Host nicht 1:1 durch (IIS-ARR setzt X-Forwarded-Host
/// nicht von selbst), schlägt der Authority-Vergleich fehl. Die Config-Liste
/// <c>Heimdall:Ui:TrustedOrigins</c> listet diese externen Origins als Trust-Anchor.
/// </summary>
public class HostTrustedOriginTests : HostBootTestBase
{
    private const string External = "https://portal.example.com";

    public HostTrustedOriginTests()
    {
        // Externes Frontend-Origin als Trust-Anchor eintragen (Env-Syntax wie die
        // übrigen Config-Keys; die Section wird lazy pro Request gelesen).
        SetEnv("Heimdall__Ui__TrustedOrigins__0", External);
        UsePathBase("/otel");
    }

    private HttpClient PostClient()
    {
        var c = CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false);
        c.DefaultRequestHeaders.Add("Origin", External);
        return c;
    }

    /// <summary>Regression: getrusteter externer Origin → Import-POST geht durch
    /// (302 auf die View, PathBase mitgetragen) statt 400 cross-origin.</summary>
    [Fact]
    public async Task Import_Post_Mit_Getrustetem_Externen_Origin_Geht_Durch()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(
            @"{ ""uid"": ""trusted-origin-test"", ""title"": ""Trusted Origin"", ""panels"": [] }"),
            "file", "dashboard.json");

        using var client = PostClient();
        var resp = await client.PostAsync("otel/dashboards/import", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/otel/otel/dashboards/trusted-origin-test", resp.Headers.Location?.ToString());
    }

    /// <summary>Import-POST mit fremdem Origin bleibt abgewiesen (CSRF-Schutz intakt).</summary>
    [Fact]
    public async Task Import_Post_Mit_Fremdem_Origin_Bleibt_400()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(@"{ ""uid"": ""evil-origin-test"", ""title"": ""x"", ""panels"": [] }"),
            "file", "dashboard.json");

        using var client = CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example.net");

        var resp = await client.PostAsync("otel/dashboards/import", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>Panel-Editor-POSTs unterliegen demselben CSRF-Check: getruster
    /// externer Origin geht durch (302, PathBase mitgetragen) …</summary>
    [Fact]
    public async Task Panel_Post_Mit_Getrustetem_Externen_Origin_Geht_Durch()
    {
        var store = Services.GetRequiredService<Heimdall.Blazor.Grafana.IGrafanaDashboardStore>();
        store.Save("trusted-panel-test", @"{ ""uid"": ""trusted-panel-test"", ""title"": ""Trusted Panel"", ""panels"": [] }");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["panelKey"] = "", ["title"] = "Neu", ["type"] = "stat",
            ["gridX"] = "0", ["gridY"] = "0", ["gridW"] = "6", ["gridH"] = "4",
            ["tgtCount"] = "1", ["t0Expr"] = "up", ["thrCount"] = "0",
        });
        using var client = PostClient();
        var resp = await client.PostAsync("otel/dashboards/trusted-panel-test/panel/save", form);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/otel/otel/dashboards/trusted-panel-test", resp.Headers.Location?.ToString());
    }

    /// <summary>… und ein fremder Origin bleibt auch hier abgewiesen (400).</summary>
    [Fact]
    public async Task Panel_Post_Mit_Fremdem_Origin_Bleibt_400()
    {
        var store = Services.GetRequiredService<Heimdall.Blazor.Grafana.IGrafanaDashboardStore>();
        store.Save("evil-panel-test", @"{ ""uid"": ""evil-panel-test"", ""title"": ""Evil Panel"", ""panels"": [] }");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["panelKey"] = "", ["title"] = "Neu", ["type"] = "stat",
            ["gridX"] = "0", ["gridY"] = "0", ["gridW"] = "6", ["gridH"] = "4",
            ["tgtCount"] = "1", ["t0Expr"] = "up", ["thrCount"] = "0",
        });
        using var client = CreateClient(new Uri("http://localhost/otel/"), allowAutoRedirect: false);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example.net");

        var resp = await client.PostAsync("otel/dashboards/evil-panel-test/panel/save", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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