using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Heimdall.Blazor.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Host-Boot-Tests fuer das Alarm-UI: /otel/alerts (Liste + Nav-Tab + Leerzustand),
/// /otel/alerts/new (Editor), POST /alerts/save (Persistenz + Redirect),
/// /alerts/{id} (Detail) und POST /alerts/{id}/delete. Isolierte Alert-Verzeichnisse
/// via Env-Vars; Alerting:Enabled bleibt false (Store + Route funktionieren ohne
/// aktiven Evaluator).
/// </summary>
public class AlertsPageTests : HostBootTestBase, IDisposable
{
    private readonly string _alertsDir;
    private readonly string[] _myEnv = { "Heimdall__Alerting__RulesDir", "Heimdall__Alerting__StateDir" };

    public AlertsPageTests()
    {
        _alertsDir = Path.Combine(Path.GetTempPath(), "heimdall-alert-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_alertsDir);
        Environment.SetEnvironmentVariable("Heimdall__Alerting__RulesDir", Path.Combine(_alertsDir, "rules"));
        Environment.SetEnvironmentVariable("Heimdall__Alerting__StateDir", _alertsDir);
    }

    // Explizite Neu-Implementierung, damit xUnit unsere Env-Vars + Temp-Dir raeumt
    // und danach die Basis (Factory + Basis-Env-Vars) disposed.
    void IDisposable.Dispose()
    {
        foreach (var k in _myEnv) Environment.SetEnvironmentVariable(k, null);
        try { if (Directory.Exists(_alertsDir)) Directory.Delete(_alertsDir, true); } catch { }
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private IAlertRuleStore RuleStore => Services.GetRequiredService<IAlertRuleStore>();

    [Fact]
    public async Task Get_Alerts_Seite_ZeigtNavTabUndLeerzustand()
    {
        var resp = await Client.GetAsync("/otel/alerts");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains(">Alerts<", html);              // Nav-Tab
        Assert.Contains("Keine Alarmregeln", html);     // Leerzustand (SeedDemoData=false)
        Assert.Contains("/otel/alerts/new", html);      // Link neue Regel
    }

    [Fact]
    public async Task Get_Alerts_Seite_ZeigtGeseedeteRegel()
    {
        var id = RuleStore.Save(AlertDemoRules.FiveXxErrorRate());

        var resp = await Client.GetAsync("/otel/alerts");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("5xx-Fehlerrate", html);
        Assert.Contains($"/otel/alerts/{id}", html);
    }

    [Fact]
    public async Task Get_Alerts_New_ZeigtEditorFormular()
    {
        var resp = await Client.GetAsync("/otel/alerts/new");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Neue Alarm-Regel", html);
        Assert.Contains("action=\"/otel/alerts/save\"", html);
        Assert.Contains("PromQL", html);
        Assert.Contains("Min. Severity", html);
    }

    [Fact]
    public async Task Post_Alerts_Save_LeitetWeiterUndPersistiert()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Save-Test",
            ["signal"] = "Metric",
            ["enabled"] = "1",
            ["promql"] = "orders_total > 0",
            ["windowSeconds"] = "300",
            ["threshold"] = "0",
            ["forSeconds"] = "5",
            ["channels"] = "logger",
            ["description"] = "test",
        });

        var resp = await ClientNoRedirect.PostAsync("/otel/alerts/save", form);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? "";
        Assert.StartsWith("/otel/alerts/", loc);

        var id = loc.Substring("/otel/alerts/".Length);
        var rule = RuleStore.Get(id);
        Assert.NotNull(rule);
        Assert.Equal("Save-Test", rule!.Name);
        Assert.Equal(AlertSignal.Metric, rule.Signal);
        Assert.True(rule.Enabled);
        Assert.Equal("orders_total > 0", rule.Promql);
        Assert.Equal(new[] { "logger" }, rule.Channels);
    }

    [Fact]
    public async Task Get_Alerts_Detail_ZeigtRegel()
    {
        var id = RuleStore.Save(AlertDemoRules.ErrorLogsSurge());

        var resp = await Client.GetAsync($"/otel/alerts/{id}");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Fehler-Logs", html);
        Assert.Contains("Zustand", html);
        Assert.Contains("/alerts/" + id + "/edit", html);   // Bearbeiten-Link
    }

    [Fact]
    public async Task Post_Alerts_Delete_LeitetWeiterUndEntferntRegel()
    {
        var id = RuleStore.Save(AlertDemoRules.FiveXxErrorRate());

        var resp = await ClientNoRedirect.PostAsync($"/otel/alerts/{id}/delete", new StringContent(""));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/otel/alerts", resp.Headers.Location?.ToString());
        Assert.Null(RuleStore.Get(id));
    }
}