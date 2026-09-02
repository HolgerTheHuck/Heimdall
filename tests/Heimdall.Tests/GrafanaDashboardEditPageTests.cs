#if NET10_0
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Heimdall.Blazor.Grafana;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Host-Boot-Tests fuer den Dashboard-Editor: Editor-Hub (/dashboards/{uid}/edit),
/// Neu-Anlage (/dashboards/new + POST /dashboards/save), Panel-/Var-Formulare und
/// ihre POST-Endpunkte sowie der rohe JSON-Modus. Kern-Assertion bleibt die
/// Verlustfreiheit: nach Panel-Saves ueberleben schemaVersion/datasource.uid/
/// fieldConfig.overrides im Raw-JSON (Editor mutiert via JsonNode).
/// </summary>
public class GrafanaDashboardEditPageTests : HostBootTestBase, IDisposable
{
    private const string Uid = "edit-ui-test";

    // Reiches Dashboard mit Parser-fremden Feldern — nach Edits muessen diese
    // Knoten noch im Raw stehen (Lossless-Vertrag auf HTTP-Ebene).
    private const string RichJson = @"{
  ""uid"": """ + Uid + @""",
  ""title"": ""Editor Test"",
  ""schemaVersion"": 40,
  ""version"": 7,
  ""editable"": true,
  ""graphTooltip"": 1,
  ""tags"": [""prod""],
  ""templating"": { ""list"": [ { ""name"": ""env"", ""type"": ""query"", ""query"": ""label_values(env)"" } ] },
  ""panels"": [
    {
      ""id"": 1,
      ""type"": ""timeseries"",
      ""title"": ""CPU"",
      ""datasource"": { ""type"": ""prometheus"", ""uid"": ""DS-HEIMDALL-1"" },
      ""fieldConfig"": {
        ""defaults"": { ""unit"": ""percent"" },
        ""overrides"": [ { ""matcher"": { ""id"": ""byName"", ""options"": ""s1"" }, ""properties"": [ { ""id"": ""color"", ""value"": { ""mode"": ""fixed"", ""fixedColor"": ""red"" } } ] } ]
      },
      ""gridPos"": { ""x"": 0, ""y"": 0, ""w"": 12, ""h"": 8 },
      ""targets"": [ { ""refId"": ""A"", ""expr"": ""cpu_used_percent"", ""datasource"": { ""type"": ""prometheus"", ""uid"": ""DS-TARGET-1"" }, ""format"": ""time_series"" } ]
    }
  ]
}";

    private readonly string[] _myEnv = { "Heimdall__DashboardsStore__Dir" };
    private readonly string _dashDir;

    public GrafanaDashboardEditPageTests()
    {
        // Isoliertes Dashboard-Verzeichnis (Basis setzt es bereits — hier nochmal
        // explizit pro Testklasse, damit Seeds nicht zwischen Klassen leaken).
        _dashDir = Path.Combine(Path.GetTempPath(), "heimdall-dash-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dashDir);
        Environment.SetEnvironmentVariable("Heimdall__DashboardsStore__Dir", _dashDir);
    }

    void IDisposable.Dispose()
    {
        foreach (var k in _myEnv) Environment.SetEnvironmentVariable(k, null);
        try { if (Directory.Exists(_dashDir)) Directory.Delete(_dashDir, true); } catch { }
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private IGrafanaDashboardStore Store => Services.GetRequiredService<IGrafanaDashboardStore>();

    private void SeedRich() => Store.Save(Uid, RichJson);

    private static Dictionary<string, string> BasePanelForm(
        string panelKey, string title = "CPU bearbeitet", string expr = "cpu_used_percent")
        => new()
        {
            ["panelKey"] = panelKey,
            ["title"] = title,
            ["type"] = "timeseries",
            ["gridX"] = "0",
            ["gridY"] = "0",
            ["gridW"] = "12",
            ["gridH"] = "8",
            ["tgtCount"] = "1",
            ["t0Expr"] = expr,
            ["t0Legend"] = "{{instance}}",
            ["thrCount"] = "1",
            ["thr0Value"] = "80",
            ["thr0Color"] = "red",
        };

    [Fact]
    public async Task ViewPage_Enthaelt_EditMenue()
    {
        SeedRich();
        var html = await Client.GetStringAsync($"/otel/dashboards/{Uid}");
        Assert.Contains("Bearbeiten", html);   // Edit-Menü (▾ wird HTML-kodiert)
        Assert.Contains($"/otel/dashboards/{Uid}/edit", html);
        Assert.Contains($"/otel/dashboards/{Uid}/json", html);
        // Cache-Buster in den noscript-Panel-Links (Panel-URLs einmalig gegen
        // Cache-Schichten, die no-store ignorieren).
        Assert.Matches(@"panel/0\?[^""]*_=\d{13}", html);
    }

    [Fact]
    public async Task EditPage_Zeigt_Hub_mit_Panel_und_Var_Zeilen()
    {
        SeedRich();
        var html = await Client.GetStringAsync($"/otel/dashboards/{Uid}/edit");
        Assert.Contains("action=\"/otel/dashboards/save\"", html);
        Assert.Contains("Panel hinzuf", html);      // + und ü werden HTML-kodiert
        Assert.Contains("Variable hinzuf", html);
        Assert.Contains("CPU", html);                                   // Panel-Titel
        Assert.Contains($"/otel/dashboards/{Uid}/panel/0/edit", html);  // Pfad-Key "0"
        Assert.Contains($"/otel/dashboards/{Uid}/var/v0/edit", html);   // Var-Key "v0"
        Assert.Contains("label_values(env)", html);                     // Var-Query
        Assert.Contains($"/otel/dashboards/{Uid}/duplicate", html);
    }

    [Fact]
    public async Task New_Dashboard_Create_Flow_Persistiert()
    {
        var html = await Client.GetStringAsync("/otel/dashboards/new");
        Assert.Contains("Neues Dashboard", html);
        Assert.Contains("action=\"/otel/dashboards/save\"", html);

        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["title"] = "Flow-Test", ["uid"] = "" });
        var resp = await ClientNoRedirect.PostAsync("/otel/dashboards/save", form);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? "";
        Assert.StartsWith("/otel/dashboards/", loc);

        var uid2 = loc["/otel/dashboards/".Length..];
        Assert.Contains(Store.List(), d => d.Title == "Flow-Test" && d.Uid == uid2);
    }

    [Fact]
    public async Task Panel_Neu_Anhaengen_Persistiert_und_Erhaelt_Fremde_Felder()
    {
        SeedRich();
        // Nebeneinander (X=12) wie der Auto-Layout-Suggest — ein Überlapp-Save
        // würde jetzt geblockt (Overlap-Check im Endpoint, siehe Overlap-Tests).
        var f = BasePanelForm(panelKey: "", title: "Neu-Panel", expr: "mem_used_percent");
        f["gridX"] = "12";
        var form = new FormUrlEncodedContent(f);
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/panel/save", form);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/otel/dashboards/{Uid}", resp.Headers.Location?.ToString());

        using var doc = JsonDocument.Parse(Store.GetRaw(Uid)!);
        var root = doc.RootElement;
        // Neu-Panel: 2 Panels, neues mit Formularwerten
        Assert.Equal(2, root.GetProperty("panels").GetArrayLength());
        var p2 = root.GetProperty("panels")[1];
        Assert.Equal("Neu-Panel", p2.GetProperty("title").GetString());
        Assert.Equal("mem_used_percent", p2.GetProperty("targets")[0].GetProperty("expr").GetString());
        // Verlustfreiheit: Parser-fremde Knoten ueberleben den Save
        Assert.Equal(40, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(7, root.GetProperty("version").GetInt32());
        Assert.Equal("prod", root.GetProperty("tags")[0].GetString());
        var p1 = root.GetProperty("panels")[0];
        Assert.Equal("DS-HEIMDALL-1", p1.GetProperty("datasource").GetProperty("uid").GetString());
        Assert.Equal("DS-TARGET-1", p1.GetProperty("targets")[0].GetProperty("datasource").GetProperty("uid").GetString());
        Assert.Equal("byName", p1.GetProperty("fieldConfig").GetProperty("overrides")[0]
            .GetProperty("matcher").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Panel_Bearbeiten_Erhaelt_Target_Datasource()
    {
        SeedRich();
        var form = new FormUrlEncodedContent(BasePanelForm(panelKey: "0", title: "CPU bearbeitet"));
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/panel/save", form);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);

        using var doc = JsonDocument.Parse(Store.GetRaw(Uid)!);
        var p1 = doc.RootElement.GetProperty("panels")[0];
        Assert.Equal("CPU bearbeitet", p1.GetProperty("title").GetString());
        // Target-Erbe: nur expr/legendFormat/instant ueberschrieben, datasource/format bleiben
        var t = p1.GetProperty("targets")[0];
        Assert.Equal("cpu_used_percent", t.GetProperty("expr").GetString());
        Assert.Equal("{{instance}}", t.GetProperty("legendFormat").GetString());
        Assert.Equal("DS-TARGET-1", t.GetProperty("datasource").GetProperty("uid").GetString());
        Assert.Equal("time_series", t.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Panel_Save_Leerer_Titel_Redirectet_Mit_Fehler()
    {
        SeedRich();
        var f = BasePanelForm(panelKey: "");
        f["title"] = "";
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/panel/save",
            new FormUrlEncodedContent(f));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith($"/otel/dashboards/{Uid}/panel/new?err=", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Panel_Save_Ohne_Targets_Redirectet_Mit_Fehler()
    {
        SeedRich();
        var f = BasePanelForm(panelKey: "");
        f["t0Expr"] = "";
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/panel/save",
            new FormUrlEncodedContent(f));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith($"/otel/dashboards/{Uid}/panel/new?err=", resp.Headers.Location?.ToString());
    }

    // --- Panel-Ebenen-Editing: Edit-Link am Panel ----------------------------

    [Fact]
    public async Task Panel_Fragment_Traegt_Edit_Link()
    {
        SeedRich();
        var html = await Client.GetStringAsync($"/otel/dashboards/{Uid}/panel/0");
        Assert.Contains($"/otel/dashboards/{Uid}/panel/0/edit", html);   // Pfad-Key "0"
        Assert.Contains("hmd-gpanel-edit", html);
    }

    // --- Live-Vorschau (GET /panel/preview, ungespeicherter Stand) -----------

    [Fact]
    public async Task Panel_Preview_Rendert_Ungespeicherten_Stand()
    {
        SeedRich();
        // GET-Submit des „Vorschau"-Buttons: alle Formularfelder in der Query,
        // nichts wird gespeichert — der Editor zeigt Formularstand + gerendertes Panel.
        var qs = "panelKey=&title=Vorschau-Panel&type=timeseries&gridX=12&gridY=0&gridW=12&gridH=8" +
                 "&tgtCount=1&t0Expr=cpu_used_percent&t0Legend=%7B%7Binstance%7D%7D" +
                 "&thrCount=1&thr0Value=80&thr0Color=red";
        var html = await Client.GetStringAsync($"/otel/dashboards/{Uid}/panel/preview?{qs}");
        Assert.Contains("Vorschau", html);
        Assert.Contains("hmd-preview", html);                    // Inline-Vorschau-Sektion
        Assert.Contains("value=\"Vorschau-Panel\"", html);      // Formularstand erhalten
        Assert.Contains("cpu_used_percent", html);              // Target-Textarea gefüllt
        // Nichts persistiert.
        Assert.DoesNotContain("Vorschau-Panel", Store.GetRaw(Uid)!);
    }

    [Fact]
    public async Task Panel_Preview_Mit_Entfernen_Haken_Droppt_Zeile()
    {
        SeedRich();
        // t0Rm=1: erste Target-Zeile fällt weg, nur t1 bleibt (im Formular + Preview).
        var qs = "panelKey=0&title=CPU&gridY=0&gridW=12&gridH=8" +
                 "&tgtCount=2&t0Expr=cpu_used_percent&t0Rm=1&t1Expr=mem_used_percent&thrCount=0";
        var html = await Client.GetStringAsync($"/otel/dashboards/{Uid}/panel/preview?{qs}");
        Assert.Contains("mem_used_percent", html);   // Zeile 1 bleibt
        Assert.DoesNotContain(">cpu_used_percent<", html);   // Zeile 0 entfernt
    }

    // --- Grid-Hygiene: Overlap-Block + Force ---------------------------------

    [Fact]
    public async Task Panel_Save_Ueberlappend_Wird_Geblockt_Force_Erlaubt()
    {
        SeedRich();
        // Neues Panel exakt auf Panel 0 (0,0,12,8) → geblockt, Meldung mit Titel.
        var f = BasePanelForm(panelKey: "", title: "Drauf");
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/panel/save",
            new FormUrlEncodedContent(f));
        var loc = Uri.UnescapeDataString(resp.Headers.Location?.ToString() ?? "");
        Assert.StartsWith($"/otel/dashboards/{Uid}/panel/new?err=", loc);
        Assert.Contains("CPU", loc);   // Titel des kollidierenden Panels in der Meldung

        // Mit force=1 („Überlappung erlauben") geht der Save durch.
        f["force"] = "1";
        var resp2 = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/panel/save",
            new FormUrlEncodedContent(f));
        Assert.Equal($"/otel/dashboards/{Uid}", resp2.Headers.Location?.ToString());
        Assert.Contains("Drauf", Store.GetRaw(Uid)!);
    }

    [Fact]
    public async Task Rename_Ehaelt_Uid_und_Aendert_Nur_Titel()
    {
        SeedRich();
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["uid"] = Uid, ["title"] = "Umbenannt" });
        var resp = await ClientNoRedirect.PostAsync("/otel/dashboards/save", form);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/otel/dashboards/{Uid}", resp.Headers.Location?.ToString());

        var raw = Store.GetRaw(Uid)!;
        Assert.Contains("\"Umbenannt\"", raw);
        Assert.Contains(Uid, raw);
        Assert.Equal("Umbenannt", Store.Get(Uid)!.Title);
    }

    [Fact]
    public async Task Duplicate_Leitet_Auf_Neue_Uid_mit_Kopie_Titel()
    {
        SeedRich();
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/duplicate", new StringContent(""));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? "";
        Assert.StartsWith("/otel/dashboards/", loc);
        var newUid = loc["/otel/dashboards/".Length..];
        Assert.NotEqual(Uid, newUid);

        var dup = Store.Get(newUid);
        Assert.NotNull(dup);
        Assert.Equal("Editor Test (Kopie)", dup!.Title);
        Assert.Equal(1, dup.Panels.Count);
    }

    [Fact]
    public async Task Panel_Delete_Veringert_Panelzahl()
    {
        SeedRich();
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/panel/0/delete", new StringContent(""));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/otel/dashboards/{Uid}/edit", resp.Headers.Location?.ToString());

        Assert.Equal(0, GrafanaDashboardEditor.ListPanels(Store.GetRaw(Uid)!).Count);
    }

    [Fact]
    public async Task Var_Save_Legt_Templating_an_und_Delete_Raeumt_Ab()
    {
        SeedRich();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["varKey"] = "", ["name"] = "region", ["type"] = "query",
            ["query"] = "label_values(region)", ["current"] = "eu-1",
            ["includeAll"] = "1", ["multi"] = "1",
        });
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/var/save", form);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/otel/dashboards/{Uid}", resp.Headers.Location?.ToString());

        using (var doc = JsonDocument.Parse(Store.GetRaw(Uid)!))
        {
            var list = doc.RootElement.GetProperty("templating").GetProperty("list");
            Assert.Equal(2, list.GetArrayLength());   // env + neu
            var v = list[1];
            Assert.Equal("region", v.GetProperty("name").GetString());
            Assert.Equal("eu-1", v.GetProperty("current").GetProperty("value").GetString());
            Assert.True(v.GetProperty("includeAll").GetBoolean());
        }

        var del = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/var/v1/delete", new StringContent(""));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, del.StatusCode);
        using (var doc = JsonDocument.Parse(Store.GetRaw(Uid)!))
        {
            var list = doc.RootElement.GetProperty("templating").GetProperty("list");
            Assert.Equal(1, list.GetArrayLength());
            Assert.Equal("env", list[0].GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Json_Editiert_Erhaelt_Fremdes_und_Erzwingt_Routen_Uid()
    {
        SeedRich();
        // Fremde UID im Text + zusaetzliches Feld: beides pruefen.
        var json = RichJson.Replace("\"Editor Test\"", "\"Umbenannt via JSON\"")
                           .Replace("\"uid\": \"" + Uid + "\"", "\"uid\": \"evil-uid\"")
                           .Replace("\"graphTooltip\": 1", "\"graphTooltip\": 1, \"newTopField\": {\"a\": 1}");
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/json",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["json"] = json }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal($"/otel/dashboards/{Uid}", resp.Headers.Location?.ToString());

        var raw = Store.GetRaw(Uid)!;
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(Uid, root.GetProperty("uid").GetString());      // Routen-Uid erzwungen
        Assert.Equal(1, root.GetProperty("newTopField").GetProperty("a").GetInt32());
        Assert.Equal("Umbenannt via JSON", root.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Json_Leer_Redirectet_Mit_Fehler()
    {
        SeedRich();
        var resp = await ClientNoRedirect.PostAsync($"/otel/dashboards/{Uid}/json",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["json"] = "  " }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith($"/otel/dashboards/{Uid}/json?err=", resp.Headers.Location?.ToString());
    }

    /// <summary>Regression: Multi-Template-Variablen rendern als Combobox
    /// (Checkbox-Panel, wie der Service-Filter) statt als meterhohes natives
    /// &lt;select multiple&gt;; Single-Variablen bleiben natives Dropdown.</summary>
    [Fact]
    public async Task ViewPage_MultiVar_Combobox_und_SingleVar_Dropdown()
    {
        Store.Save("var-ui-test", @"{
  ""uid"": ""var-ui-test"", ""title"": ""Var UI"", ""panels"": [],
  ""templating"": { ""list"": [
    { ""name"": ""env"", ""type"": ""custom"", ""query"": ""dev,staging,prod"", ""multi"": true, ""includeAll"": true },
    { ""name"": ""region"", ""type"": ""custom"", ""query"": ""eu,us"", ""multi"": false }
  ] }
}");

        var html = await Client.GetStringAsync("/otel/dashboards/var-ui-test");
        // Multi-Var env: Combobox mit Checkboxen (unchecked = $__all)
        Assert.Contains("details class=\"hmd-msel\"", html);
        Assert.Contains("name=\"var-env\"", html);
        Assert.Contains("value=\"staging\"", html);
        // Single-Var region: natives Dropdown ohne multiple
        Assert.Contains("<select name=\"var-region\">", html);
        Assert.DoesNotContain("multiple", html);
    }
}
#endif