using System;
using System.Collections.Generic;
using Heimdall.Blazor.Grafana;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Paritäts-Tests für <see cref="GrafanaDashboardRender"/>: die Helfer wurden
/// 1:1 aus der früher inline in <c>GrafanaDashboardViewPage.OnInitialized</c>
/// stehenden Logik extrahiert und werden nun von Shell und Per-Panel-Endpoint
/// geteilt. Diese Tests sichern, dass Zeitbereich/Step/Render-Variablen und die
/// Slot-Liste (Y-then-X-Sortierung + Repeat-Expansion) wie bisher berechnet
/// werden — die Shell rendert sonst Platzhalter gegen falsche Indices und der
/// Endpoint würde das falsche Panel auswerten.
/// </summary>
public class GrafanaDashboardRenderTests
{
    private const long NanosPerSecond = 1_000_000_000L;

    private static GrafanaDashboard Dash(
        IReadOnlyList<GrafanaPanel>? panels = null,
        IReadOnlyList<GrafanaTemplatingVar>? templating = null)
        => new("u", "D", panels ?? Array.Empty<GrafanaPanel>(),
            templating ?? Array.Empty<GrafanaTemplatingVar>(), null, null);

    private static GrafanaPanel Panel(string title, int x, int y, int w = 6, string? repeat = null)
        => new(1, title, "stat", new GrafanaGridPos(x, y, w, 8),
            Array.Empty<GrafanaTarget>(), null, null, repeat, "prometheus", null);

    // === BuildRenderVars: Zeitbereich + Step ===============================

    [Fact]
    public void BuildRenderVars_Preset1h_LiefertStundenFensterUndStep()
    {
        long now = 10_000L * NanosPerSecond;          // 10_000 s
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(), null, "1h", null, null, now);

        // 1h-Preset: from = now - 3600s, to = now → 3_600_000 ms Fenster.
        Assert.Equal((now - 3_600L * NanosPerSecond) / 1_000_000L, prep.FromMs);
        Assert.Equal(now / 1_000_000L, prep.ToMs);
        // step = Fenster / 120 = 3_600_000 / 120 = 30_000 ms (≥ 1_000 Floor).
        Assert.Equal(30_000L, prep.StepMs);
    }

    [Fact]
    public void BuildRenderVars_StepFloor_Mindestens1000ms()
    {
        // Sehr kleines Fenster (< 120 s) → (to-from)/120 < 1000 → Floor 1000.
        long now = 10_000L * NanosPerSecond;
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(), null, null,
            now - 60L * NanosPerSecond, now, now);
        Assert.Equal(1_000L, prep.StepMs);
    }

    [Fact]
    public void BuildRenderVars_ExpliziteFromTo_UeberschreibtPreset()
    {
        long from = 2_000L * NanosPerSecond, to = 5_000L * NanosPerSecond;
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(), null, "1h", from, to, 0L);
        Assert.Equal(2_000_000L, prep.FromMs);   // 2_000 s → 2_000_000 ms
        Assert.Equal(5_000_000L, prep.ToMs);
        Assert.Equal(25_000L, prep.StepMs);      // 3_000_000 ms / 120 = 25_000
    }

    [Fact]
    public void BuildRenderVars_BuiltIns_WerdenAbgeleitet()
    {
        long now = 10_000L * NanosPerSecond;
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(), null, "1h", null, null, now);
        // 1h-Fenster, step 30 s: __interval=30s, __rate_interval=max(30s*4,60s)=2m,
        // __range=1h (gesamter Zeitraum).
        Assert.Equal("30s", prep.RenderVars["__interval"]);
        Assert.Equal("2m", prep.RenderVars["__rate_interval"]);
        Assert.Equal("1h", prep.RenderVars["__range"]);
    }

    // === BuildRenderVars: Template-Variablen ==============================

    [Fact]
    public void BuildRenderVars_Variable_UebernimmtQueryAuswahl()
    {
        var templating = new[]
        {
            new GrafanaTemplatingVar("job", "query", "label_values(up, job)", "shop", true, false),
        };
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(templating: templating),
            new Dictionary<string, string> { ["job"] = "billing" }, "1h", null, null, 0L);
        Assert.Equal("billing", prep.RenderVars["job"]);
    }

    [Fact]
    public void BuildRenderVars_Variable_FehltAuswahl_NimmtDefault()
    {
        var templating = new[]
        {
            new GrafanaTemplatingVar("job", "query", "label_values(up, job)", "shop", true, false),
        };
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(templating: templating),
            null, "1h", null, null, 0L);
        Assert.Equal("shop", prep.RenderVars["job"]);   // CurrentValue als Default
    }

    [Fact]
    public void BuildRenderVars_Variable_OhneDefault_WirdAll()
    {
        var templating = new[]
        {
            new GrafanaTemplatingVar("job", "query", "label_values(up, job)", null, true, false),
        };
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(templating: templating),
            null, "1h", null, null, 0L);
        Assert.Equal("$__all", prep.RenderVars["job"]);
    }

    [Fact]
    public void BuildRenderVars_SkipptDatasourceUndLeereNamen()
    {
        var templating = new[]
        {
            new GrafanaTemplatingVar("", "query", "x", "v", false, false),
            new GrafanaTemplatingVar("ds", "datasource", "x", "v", false, false),
            new GrafanaTemplatingVar("job", "query", "x", "shop", false, false),
        };
        var prep = GrafanaDashboardRender.BuildRenderVars(Dash(templating: templating),
            null, "1h", null, null, 0L);
        Assert.False(prep.RenderVars.ContainsKey(""));
        Assert.False(prep.RenderVars.ContainsKey("ds"));
        Assert.Equal("shop", prep.RenderVars["job"]);
    }

    // === ExpandPanels: Sortierung + Repeat-Expansion ======================

    [Fact]
    public void ExpandPanels_SortiertYDannX()
    {
        var panels = new[]
        {
            Panel("A", x: 6, y: 1),
            Panel("B", x: 0, y: 0),
            Panel("C", x: 0, y: 1),
        };
        var renderVars = new Dictionary<string, string> { ["__interval"] = "30s" };
        var slots = GrafanaDashboardRender.ExpandPanels(Dash(panels), renderVars);

        Assert.Equal(3, slots.Count);
        Assert.Equal("B", slots[0].Title);    // Y=0
        Assert.Equal("C", slots[1].Title);    // Y=1, X=0 (vor X=6)
        Assert.Equal("A", slots[2].Title);    // Y=1, X=6
    }

    [Fact]
    public void ExpandPanels_Repeat_ProWertEinSlot()
    {
        var panels = new[]
        {
            Panel("C ${region}", x: 0, y: 0, repeat: "region"),
        };
        var renderVars = new Dictionary<string, string> { ["region"] = "eu,us" };
        var slots = GrafanaDashboardRender.ExpandPanels(Dash(panels), renderVars);

        Assert.Equal(2, slots.Count);
        Assert.Equal("C eu", slots[0].Title);
        Assert.Equal("eu", slots[0].Vars["region"]);
        Assert.Equal("C us", slots[1].Title);
        Assert.Equal("us", slots[1].Vars["region"]);
    }

    [Fact]
    public void ExpandPanels_Repeat_EinWert_BleibtEinSlot()
    {
        var panels = new[] { Panel("C ${region}", x: 0, y: 0, repeat: "region") };
        var renderVars = new Dictionary<string, string> { ["region"] = "eu" };
        var slots = GrafanaDashboardRender.ExpandPanels(Dash(panels), renderVars);
        Assert.Single(slots);
        Assert.Equal("C eu", slots[0].Title);
    }

    [Fact]
    public void ExpandPanels_Repeat_LeerOderFehlend_BleibtEinSlot()
    {
        var panels = new[] { Panel("C ${region}", x: 0, y: 0, repeat: "region") };
        // Repeat-Variable nicht in renderVars → ein Slot gegen baseVars (Token bleibt).
        var slots = GrafanaDashboardRender.ExpandPanels(Dash(panels),
            new Dictionary<string, string> { ["__interval"] = "30s" });
        Assert.Single(slots);
        Assert.Equal("C ${region}", slots[0].Title);
    }

    [Fact]
    public void ExpandPanels_IndexIstStabilerSchluessel()
    {
        // Drei Panels (eins mit Repeat-Expansion) → 4 Slots; der Index in die
        // Liste ist der Schlüssel, den der Per-Panel-Endpoint nutzt.
        var panels = new[]
        {
            Panel("B", x: 0, y: 0),
            Panel("R ${region}", x: 0, y: 1, repeat: "region"),
            Panel("A", x: 6, y: 1),
        };
        var renderVars = new Dictionary<string, string> { ["region"] = "eu,us" };
        var slots = GrafanaDashboardRender.ExpandPanels(Dash(panels), renderVars);

        Assert.Equal(4, slots.Count);
        Assert.Equal("B", slots[0].Title);
        Assert.Equal("R eu", slots[1].Title);
        Assert.Equal("R us", slots[2].Title);
        Assert.Equal("A", slots[3].Title);
    }
}