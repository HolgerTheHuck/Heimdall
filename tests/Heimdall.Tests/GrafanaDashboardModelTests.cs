using System;
using System.IO;
using System.Linq;
using Heimdall.Blazor.Grafana;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer den lenienten Grafana-Dashboard-JSON-Parser
/// (<see cref="GrafanaDashboardModel"/>). Ueberprueft das echte
/// heimdall-overview.json-Modell sowie synthetische Dashboards mit
/// allen Panel-Typen, fehlenden Feldern und kaputtem JSON.
/// </summary>
public class GrafanaDashboardModelTests
{
    private static readonly string RepoGrafana = Locate("grafana", "heimdall-overview.json");

    private static string Locate(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var d = dir; d is not null; d = Directory.GetParent(d)?.FullName)
        {
            var cand = Path.Combine(d, Path.Combine(parts));
            if (File.Exists(cand)) return cand;
        }
        return Path.Combine(parts);
    }

    [Fact]
    public void Parse_Null_Leer_Kaputt_LiefertNull()
    {
        Assert.Null(GrafanaDashboardModel.Parse(null));
        Assert.Null(GrafanaDashboardModel.Parse(""));
        Assert.Null(GrafanaDashboardModel.Parse("   "));
        Assert.Null(GrafanaDashboardModel.Parse("gar kein json"));
        Assert.Null(GrafanaDashboardModel.Parse("{ kaputtes"));
    }

    [Fact]
    public void Parse_HeimdallOverview_Vollstaendig()
    {
        if (!File.Exists(RepoGrafana)) return; // Repo-Pfad optional
        var json = File.ReadAllText(RepoGrafana);
        var dash = GrafanaDashboardModel.Parse(json);
        Assert.NotNull(dash);
        Assert.Equal("heimdall-overview", dash!.Uid);
        Assert.Equal("Heimdall Overview (RED + Application Metrics)", dash.Title);
        Assert.Equal(9, dash.Panels.Count);
        Assert.Equal("now-1h", dash.TimeFrom);
        Assert.Equal("now", dash.TimeTo);

        // Templating: 3 Vars (DS_HEIMDALL, job, http_route)
        Assert.Equal(3, dash.Templating.Count);
        var job = dash.Templating.Single(v => v.Name == "job");
        Assert.Equal("query", job.Type);
        Assert.Contains("label_values(http_requests_total, job)", job.Query);
        Assert.True(job.IncludeAll);
        Assert.True(job.Multi);
        var route = dash.Templating.Single(v => v.Name == "http_route");
        Assert.Contains("http_route", route.Query);

        // Panel-Typen: 3x stat, 5x timeseries, 1x table.
        Assert.Equal(3, dash.Panels.Count(p => p.Kind == GrafanaPanelKind.Stat));
        Assert.Equal(5, dash.Panels.Count(p => p.Kind == GrafanaPanelKind.Timeseries));
        Assert.Equal(1, dash.Panels.Count(p => p.Kind == GrafanaPanelKind.Table));

        // Panel 1 (stat) mit Thresholds + Unit + gridPos.
        var p1 = dash.Panels.Single(p => p.Id == 1);
        Assert.Equal(GrafanaPanelKind.Stat, p1.Kind);
        Assert.Equal("reqps", p1.FieldConfig!.Unit);
        var steps = p1.FieldConfig.Thresholds!;
        Assert.Equal(3, steps.Count);
        Assert.Null(steps[0].Value); // Basis (green)
        Assert.Equal(50, steps[1].Value);
        Assert.Equal(200, steps[2].Value);
        Assert.Equal(new[] { 0, 0, 4, 4 }, new[] { p1.GridPos.X, p1.GridPos.Y, p1.GridPos.W, p1.GridPos.H });
        Assert.Single(p1.Targets);
        Assert.Contains("sum(rate(http_requests_total", p1.Targets[0].Expr);
        Assert.Equal("rps", p1.Targets[0].LegendFormat);

        // Panel 6 hat 3 Targets (p50/p95/p99).
        var p6 = dash.Panels.Single(p => p.Id == 6);
        Assert.Equal(3, p6.Targets.Count);
        Assert.Equal(new[] { "p50", "p95", "p99" }, p6.Targets.Select(t => t.LegendFormat).ToArray());

        // Panel 9 (table) mit Transformation + instant.
        var p9 = dash.Panels.Single(p => p.Id == 9);
        Assert.Equal(GrafanaPanelKind.Table, p9.Kind);
        Assert.True(p9.Targets[0].Instant);
        Assert.Equal("table", p9.Targets[0].Format);
        Assert.NotNull(p9.Transformations);
        Assert.Equal("organize", p9.Transformations![0].Id);
    }

    [Fact]
    public void KindOf_OrdnetAlleTypen()
    {
        Assert.Equal(GrafanaPanelKind.Timeseries, GrafanaDashboardModel.KindOf("timeseries"));
        Assert.Equal(GrafanaPanelKind.Timeseries, GrafanaDashboardModel.KindOf("graph"));
        Assert.Equal(GrafanaPanelKind.Stat, GrafanaDashboardModel.KindOf("stat"));
        Assert.Equal(GrafanaPanelKind.Stat, GrafanaDashboardModel.KindOf("singlestat"));
        Assert.Equal(GrafanaPanelKind.Table, GrafanaDashboardModel.KindOf("table"));
        Assert.Equal(GrafanaPanelKind.BarGauge, GrafanaDashboardModel.KindOf("bargauge"));
        Assert.Equal(GrafanaPanelKind.Gauge, GrafanaDashboardModel.KindOf("gauge"));
        Assert.Equal(GrafanaPanelKind.Pie, GrafanaDashboardModel.KindOf("pie"));
        Assert.Equal(GrafanaPanelKind.Pie, GrafanaDashboardModel.KindOf("piechart"));
        Assert.Equal(GrafanaPanelKind.Unknown, GrafanaDashboardModel.KindOf("alertlist"));
        Assert.Equal(GrafanaPanelKind.Unknown, GrafanaDashboardModel.KindOf(""));
        Assert.Equal(GrafanaPanelKind.Unknown, GrafanaDashboardModel.KindOf(null!));
    }

    [Fact]
    public void Parse_Synthetisch_BargaugeGaugePie_FehlendeFelder()
    {
        var json = """
        {
          "title": "Mix",
          "panels": [
            { "id": 1, "type": "bargauge", "title": "BG", "gridPos": {"h":6,"w":6,"x":0,"y":0},
              "targets": [{"expr":"topk(5, sum by (http_route) (rate(http_requests_total[5m])))"}] },
            { "id": 2, "type": "gauge", "title": "G", "gridPos": {"h":6,"w":6,"x":6,"y":0},
              "targets": [{"expr":"sum(rate(http_requests_total[5m]))"}],
              "fieldConfig": {"defaults": {"unit":"reqps", "thresholds": {"steps": [{"color":"green"},{"color":"yellow","value":50}]}} } },
            { "id": 3, "type": "piechart", "title": "P", "gridPos": {"h":6,"w":12,"x":0,"y":6},
              "targets": [{"expr":"sum by (http_route) (rate(http_requests_total[5m]))", "legendFormat":"{{http_route}}"}] },
            { "id": 4, "type": "timeseries", "title": "ohne gridPos/targets",
              "targets": [] }
          ]
        }
        """;
        var dash = GrafanaDashboardModel.Parse(json);
        Assert.NotNull(dash);
        Assert.Equal("Mix", dash!.Title);
        Assert.Equal(4, dash.Panels.Count);
        Assert.Equal(GrafanaPanelKind.BarGauge, dash.Panels[0].Kind);
        Assert.Equal(GrafanaPanelKind.Gauge, dash.Panels[1].Kind);
        Assert.Equal(GrafanaPanelKind.Pie, dash.Panels[2].Kind);
        Assert.Equal(GrafanaPanelKind.Timeseries, dash.Panels[3].Kind);
        // Panel ohne uid-Feld am Dashboard -> generierte UID (stabil, non-empty).
        Assert.False(string.IsNullOrEmpty(dash.Uid));
        Assert.StartsWith("d", dash.Uid);

        // gauge-Thresholds + Unit.
        var g = dash.Panels[1];
        Assert.Equal("reqps", g.FieldConfig!.Unit);
        Assert.Equal(2, g.FieldConfig.Thresholds!.Count);
        Assert.Null(g.FieldConfig.Thresholds[0].Value);
        Assert.Equal(50, g.FieldConfig.Thresholds[1].Value);

        // Panel 4 ohne gridPos -> Zero, ohne Targets -> leere Liste (nicht null).
        Assert.Equal(GrafanaGridPos.Zero, dash.Panels[3].GridPos);
        Assert.Empty(dash.Panels[3].Targets);
    }

    [Fact]
    public void Parse_Templating_CurrentValueAlsArrayUndString()
    {
        var json = """
        {
          "uid": "tpl", "title": "T", "panels": [],
          "templating": { "list": [
            { "name": "job", "type": "query", "query": "label_values(http_requests_total, job)",
              "current": {"text":"All","value":["a","b"]}, "includeAll": true, "multi": true },
            { "name": "env", "type": "custom", "query": "dev,prod",
              "current": {"text":"dev","value":"dev"}, "includeAll": false, "multi": false }
          ]}
        }
        """;
        var dash = GrafanaDashboardModel.Parse(json);
        Assert.NotNull(dash);
        Assert.Equal(2, dash!.Templating.Count);
        Assert.Equal("a,b", dash.Templating[0].CurrentValue);
        Assert.Equal("dev", dash.Templating[1].CurrentValue);
        Assert.False(dash.Templating[1].Multi);
    }

    [Fact]
    public void Parse_LeeresObjekt_LiefertLeeresDashboard()
    {
        var dash = GrafanaDashboardModel.Parse("{}");
        Assert.NotNull(dash);
        Assert.Empty(dash!.Panels);
        Assert.Empty(dash.Templating);
        Assert.False(string.IsNullOrEmpty(dash.Uid)); // generiert
    }
}