#if NET10_0
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Heimdall;
using Heimdall.Blazor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// „Dashboard ohne Alerting“ — die Minimalkonfiguration, durch die die Alert-
/// Store-Falle schlüpfte: <c>AddHeimdallDashboard(sink)</c> + <c>MapHeimdallDashboard</c>
/// OHNE <c>AddHeimdallAlerting</c>. Vor dem Fix mappten die /alerts-POSTs Handler
/// mit <c>IAlertRuleStore</c>/<c>IAlertStateStore</c>-Parametern, die im Container
/// fehlten — die RequestDelegateFactory warf beim ERSTEN Request
/// („Failure to infer one or more parameters“) und riss die Routing-Tabelle des
/// GESAMTEN Hosts mit: jede Route antwortete 500. Fix: AddHeimdallDashboard
/// registriert Store-Defaults (TryAdd); MapHeimdallDashboard wirft Fail-Fast,
/// wenn die Stores fehlen (z. B. Mapping ohne AddHeimdallDashboard).
/// </summary>
public class DashboardWithoutAlertingTests : IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>Minimale Query (leere Signale) für die Dashboard-Seiten.</summary>
    private sealed class EmptyQuery : IHeimdallQuery
    {
        public IReadOnlyList<TraceSummary> ListTraces(TraceFilter f) => Array.Empty<TraceSummary>();
        public IReadOnlyList<SpanRow> GetTrace(string t) => Array.Empty<SpanRow>();
        public IReadOnlyList<LogRow> SearchLogs(LogSearch s) => Array.Empty<LogRow>();
        public IReadOnlyList<SpanRow> ListSpans(SpanFilter f) => Array.Empty<SpanRow>();
        public IReadOnlyList<MetricRow> MetricSeries(string n, long? f, long? t, int lim = 500) => Array.Empty<MetricRow>();
        public long CountSpans() => 0;
        public long CountLogs() => 0;
        public long CountMetrics() => 0;
    }

    [Fact]
    public async Task Dashboard_Ohne_Alerting_Bootet_und_Antwortet()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHeimdallDashboard(new EmptyQuery());

        var app = builder.Build();
        app.MapHeimdallDashboard("/otel");
        await app.StartAsync();
        _app = app;

        var client = app.GetTestClient();

        // Beliebige Route — nicht nur /alerts/*: Vor dem Fix warf die
        // Routing-Initialisierung beim ersten Request und jeder Pfad lief 500.
        var traces = await client.GetAsync("/otel/traces");
        Assert.Equal(HttpStatusCode.OK, traces.StatusCode);

        var alerts = await client.GetAsync("/otel/alerts");
        Assert.Equal(HttpStatusCode.OK, alerts.StatusCode);

        var home = await client.GetAsync("/otel");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Mapping_Ohne_Stores_FailFast_Beim_Mapping()
    {
        // Verbleibender Pfad in die Falle: MapHeimdallDashboard OHNE jede
        // DI-Registrierung. Der Fail-Fast-Check in MapHeimdallDashboard wirft
        // SOFORT (Startzeit) statt beim ersten Request die Routing-Tabelle zu
        // sprengen — Meldung nennt Ursache und Handlungsanweisung.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapHeimdallDashboard("/otel"));
        Assert.Contains("IAlertRuleStore", ex.Message);
        Assert.Contains("AddHeimdallAlerting", ex.Message);

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
#endif