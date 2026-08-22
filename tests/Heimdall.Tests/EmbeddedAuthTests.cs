#if NET10_0
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Heimdall.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Embedded-Auth (gehobene <see cref="HeimdallAuthMiddleware"/> aus
/// <c>Heimdall.AspNetCore</c>) an einer minimalen Pipeline OHNE SQLite/Dashboard.
/// Beweist: mit <see cref="HeimdallAuthOptions.ProtectedPrefix"/>="/otel" wird nur
/// die Heimdall-Oberfläche geschützt, die App-eigenen Routes (<c>/api/foo</c>)
/// bleiben frei; die Prom-API (<c>/otel/api/v1/*</c>) verlangt den ApiKey;
/// <see cref="HeimdallAuthOptions.Username"/> wird geprüft; <c>Enabled=false</c>
/// liefert Zero-Overhead-Passthrough. Komplementär zu <see cref="HostAuthTests"/>
/// (das den globalen Host-Pfad via <c>WebApplicationFactory</c> prüft).
/// </summary>
public class EmbeddedAuthTests : IAsyncDisposable
{
    private const string User = "admin";
    private const string Pw = "secret";
    private const string Key = "k";

    private WebApplication? _app;

    /// <summary>Opt-in Auth wie im OtelSample (ProtectedPrefix="/otel").</summary>
    private static HeimdallAuthOptions Enabled() => new()
    {
        Enabled = true,
        Username = User,
        Password = Pw,
        ApiKey = Key,
        ProtectedPrefix = "/otel",
    };

    /// <summary>Startet eine minimale In-Memory-Pipeline (TestServer) mit UseHeimdallAuth.</summary>
    private async Task<HttpClient> BuildClientAsync(HeimdallAuthOptions auth)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();          // In-Memory-Host statt Kestrel
        var app = builder.Build();
        app.UseHeimdallAuth(auth);
        app.Run(async ctx =>
        {
            var p = ctx.Request.Path.Value ?? string.Empty;
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync(p == "/otel" ? "otel-ui"
                : p == "/api/foo" ? "app-route"
                : p.StartsWith("/otel/api/v1/", StringComparison.OrdinalIgnoreCase) ? "prom-api"
                : "other");
        });
        await app.StartAsync();
        _app = app;
        return app.GetTestClient();
    }

    [Fact]
    public async Task AppRoute_OhneCreds_Liefert200_Passthrough()
    {
        // Embedded-Kern-Beweis: App-eigene Route steht NICHT unter ProtectedPrefix
        // → passiert trotz Enabled=true unverändert (kein Login nötig).
        var client = await BuildClientAsync(Enabled());
        var resp = await client.GetAsync("/api/foo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("app-route", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Otel_OhneCreds_Liefert401_MitBasicChallenge()
    {
        var client = await BuildClientAsync(Enabled());
        var resp = await client.GetAsync("/otel");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Basic", resp.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Otel_MitKorrektemBasicAuth_Liefert200()
    {
        var client = await BuildClientAsync(Enabled());
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", Base64($"{User}:{Pw}"));
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Otel_MitFalschemUsername_Liefert401()
    {
        var client = await BuildClientAsync(Enabled());
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", Base64($"wrong:{Pw}"));
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]      //capital-first — die natürliche Eingabe, die den Bug auslöste
    [InlineData("ADMIN")]
    [InlineData("aDmIn")]
    public async Task Otel_UsernameCaseInsensitiv_Liefert200(string userVariant)
    {
        // Username ist case-insensitiv („Admin" == „admin") — Usernamen merkt man
        // sich ohne exakte Groß-/Kleinschreibung. Passwort bleibt case-sensitiv (s. u.).
        // Regression: früher war der Username case-sensitiv → „Admin"/„admin" → 401.
        var client = await BuildClientAsync(Enabled());
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", Base64($"{userVariant}:{Pw}"));
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Otel_PasswortCaseSensitiv_FalscheCase_Liefert401()
    {
        // Passwort bleibt case-sensitiv (Secret ist ein Secret) — „Secret" ≠ „secret".
        var client = await BuildClientAsync(Enabled());
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", Base64($"{User}:Secret"));
        var resp = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PromApi_OhneApiKey_Liefert401_MitKey_Liefert200()
    {
        var client = await BuildClientAsync(Enabled());

        var noKey = await client.GetAsync("/otel/api/v1/query?query=up");
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        using var msg = new HttpRequestMessage(HttpMethod.Get, "/otel/api/v1/query?query=up");
        msg.Headers.Add("x-heimdall-key", Key);
        var withKey = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
    }

    [Fact]
    public async Task Disabled_LiefertAlles200_Passthrough()
    {
        // Status quo: Enabled=false → Zero-Overhead-Passthrough auf allen Pfaden.
        var client = await BuildClientAsync(new HeimdallAuthOptions { Enabled = false });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/otel")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/foo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/otel/api/v1/query")).StatusCode);
    }

    private static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.DisposeAsync(); _app = null; }
    }
}
#endif