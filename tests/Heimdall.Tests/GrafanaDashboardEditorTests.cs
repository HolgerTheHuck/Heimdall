using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heimdall.Blazor.Grafana;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer den Dashboard-Editor (<see cref="GrafanaDashboardEditor"/>). Kern ist der
/// Lossless-Roundtrip: reiches importiertes Dashboard-JSON → SetTitle/UpsertPanel/
/// UpsertVariable → alle Felder, die der (verlustbehaftete) Parser NICHT liest, sind
/// noch vorhanden (datasource.uid, fieldConfig.overrides, options, schemaVersion,
/// transformations, Target-format). Ausserdem Key-Mapping (inkl. rows[]-Dashboards),
/// Neu-Anlage-Konventionen (id, gridPos.y, refId) und Uid-Generierung.
/// </summary>
public class GrafanaDashboardEditorTests
{
    // Reiches Dashboard: Felder, die der Parser NICHT liest, sind markiert und
    // muessen jede Editor-Mutation ueberleben.
    private const string RichJson = """
        {
          "uid": "rich", "title": "Rich", "schemaVersion": 39, "version": 7,
          "editable": true, "graphTooltip": 1,
          "tags": ["prod", "svc"],
          "time": { "from": "now-6h", "to": "now" },
          "templating": { "list": [
            { "name": "job", "type": "query", "current": { "text": "All", "value": "$__all" },
              "query": "label_values(http_requests_total, job)", "includeAll": true, "multi": true }
          ] },
          "panels": [
            { "id": 1, "type": "timeseries", "title": "RPS",
              "description": "Anfragen pro Sekunde",
              "gridPos": { "h": 8, "w": 12, "x": 0, "y": 0 },
              "datasource": { "type": "prometheus", "uid": "DS-HEIMDALL-1" },
              "targets": [ { "expr": "sum(rate(http_requests_total[$__rate_interval]))",
                             "legendFormat": "{{job}}", "refId": "A", "format": "time_series",
                             "interval": "1m",
                             "datasource": { "type": "prometheus", "uid": "DS-TARGET-1" } } ],
              "fieldConfig": { "defaults": { "unit": "reqps", "decimals": 2,
                                "thresholds": { "mode": "absolute",
                                  "steps": [ { "color": "green", "value": null } ] } },
                                "overrides": [ { "matcher": { "id": "byName", "options": "rps" },
                                  "properties": [ { "id": "links", "value": [ { "title": "Logs", "url": "/x?k=${__data.fields.job}" } ] } ] } ] },
              "options": { "legend": { "displayMode": "list" }, "tooltip": { "mode": "multi" } },
              "transformations": [ { "id": "organize", "options": { "excludeByName": {} } } ] },
            { "id": 2, "type": "row", "title": "Sektion",
              "gridPos": { "h": 1, "w": 24, "x": 0, "y": 8 },
              "panels": [
                { "id": 3, "type": "stat", "title": "Fehlerrate",
                  "gridPos": { "h": 4, "w": 6, "x": 0, "y": 9 },
                  "datasource": { "type": "prometheus", "uid": "DS-HEIMDALL-2" },
                  "targets": [ { "expr": "sum(rate(errors_total[5m]))", "refId": "A" } ],
                  "fieldConfig": { "defaults": { "unit": "percent" }, "overrides": [] } }
              ] }
          ]
        }
        """;

    private static JsonNode Parse(string json) =>
        JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON kaputt");

    // --- Lossless-Roundtrip (Kerntest) ---------------------------------------

    [Fact]
    public void Editiertes_Rich_Dashboard_Erhaelt_Parser_fremde_Felder()
    {
        // Titel aendern.
        var renamed = GrafanaDashboardEditor.SetTitle(RichJson, "Rich neu");
        Assert.Contains("\"title\": \"Rich neu\"", renamed);
        Assert.Contains("\"uid\": \"rich\"", renamed);

        // Bestehendes Panel (Key "0") editieren: nur Titel + Unit.
        var form = GrafanaDashboardEditor.ReadPanel(renamed, "0")!;
        Assert.Equal("RPS", form.Title);
        var updated = GrafanaDashboardEditor.UpsertPanel(renamed, "0", form with
        {
            Title = "RPS neu", Unit = "reqps",
            Targets = new[] { form.Targets[0] with { Expr = "sum(rate(x_total[5m]))" } },
        });

        var root = Parse(updated);

        // Intendierte Aenderungen.
        Assert.Equal("Rich neu", (string?)root["title"]);
        Assert.Equal("RPS neu", (string?)root["panels"]![0]!["title"]);
        Assert.Equal("sum(rate(x_total[5m]))", (string?)root["panels"]![0]!["targets"]![0]!["expr"]);

        // Parser-fremde Felder ueberleben ALLE.
        Assert.Equal(39, (int?)root["schemaVersion"]);
        Assert.Equal(7, (int?)root["version"]);
        Assert.True((bool?)root["editable"]);
        Assert.Equal(1, (int?)root["graphTooltip"]);
        var tagList = root["tags"] as JsonArray;
        Assert.NotNull(tagList);
        Assert.Equal(2, tagList!.Count);

        var p0 = root["panels"]![0]!;
        Assert.Equal("DS-HEIMDALL-1", (string?)p0["datasource"]!["uid"]);
        Assert.Equal("Anfragen pro Sekunde", (string?)p0["description"]);
        Assert.Equal("reqps", (string?)p0["fieldConfig"]!["defaults"]!["unit"]);
        Assert.Equal(2, (int?)p0["fieldConfig"]!["defaults"]!["decimals"]);
        Assert.NotNull(p0["fieldConfig"]!["overrides"]);   // Links-Override unangetastet
        Assert.Contains("Logs", p0["fieldConfig"]!["overrides"]!.ToJsonString());
        Assert.NotNull(p0["options"]!["legend"]);
        Assert.Equal("multi", (string?)p0["options"]!["tooltip"]!["mode"]);
        Assert.NotNull(p0["transformations"]);
        Assert.Equal("time_series", (string?)p0["targets"]![0]!["format"]);
        Assert.Equal("1m", (string?)p0["targets"]![0]!["interval"]);
        Assert.Equal("A", (string?)p0["targets"]![0]!["refId"]);

        // Row + Kind-Panel unveraendert.
        var p1 = root["panels"]![1]!;
        Assert.Equal("row", (string?)p1["type"]);
        Assert.Equal("DS-HEIMDALL-2", (string?)p1["panels"]![0]!["datasource"]!["uid"]);

        // Templating-Var editieren: query als Plain-String, current als Objekt.
        var withVar = GrafanaDashboardEditor.UpsertVariable(updated, "v0",
            new GrafanaDashboardEditor.VariableForm("job", "query", "label_values(up, job)", "$__all", true, true));
        var root2 = Parse(withVar);
        var job = root2["templating"]!["list"]![0]!;
        Assert.Equal("label_values(up, job)", (string?)job["query"]);
        Assert.Equal("$__all", (string?)job["current"]!["value"]);
    }

    [Fact]
    public void UpsertPanel_Ehaelt_Existing_Target_Objekte_per_Index()
    {
        // Ziel des Target-Erbens: datasource.uid/format/interval des Bestands-Targets.
        var updated = GrafanaDashboardEditor.UpsertPanel(RichJson, "0",
            GrafanaDashboardEditor.ReadPanel(RichJson, "0")! with
            {
                Targets = new[] { new GrafanaDashboardEditor.TargetForm("up", null, false) },
            });
        var t = Parse(updated)["panels"]![0]!["targets"]![0]!;
        Assert.Equal("up", (string?)t["expr"]);
        // Target-Objekt per Index geerbt: Target-datasource + Format/Intervall bleiben.
        Assert.Equal("DS-TARGET-1", (string?)t["datasource"]!["uid"]);
        Assert.Equal("time_series", (string?)t["format"]);
        Assert.Equal("1m", (string?)t["interval"]);
        Assert.Null((string?)t["legendFormat"]);   // geleert → Property entfernt
    }

    // --- Neu-Anlage -------------------------------------------------------------

    [Fact]
    public void UpsertPanel_Neu_Uebernimmt_Form_Y()
    {
        // Neu-Anlage übernimmt das Form-Y (kein Zwangs-MaxBottom mehr): die
        // Suggest-Position liefert die Page (SuggestNewPos), Kollisionen blockt
        // der Overlap-Check im Save-Endpoint.
        var added = GrafanaDashboardEditor.UpsertPanel(RichJson, null, new GrafanaDashboardEditor.PanelForm(
            "Neu", "stat", 0, 13, 6, 4,
            new[] { new GrafanaDashboardEditor.TargetForm("up", "up", false) },
            "short", new[] { new GrafanaDashboardEditor.ThresholdForm(null, "green"), new GrafanaDashboardEditor.ThresholdForm(50, "red") },
            null, "area"));
        var root = Parse(added);
        var p = root["panels"]![2]!;
        Assert.Equal(4, (int?)p["id"]);              // max bestehender Ids (1,3) + 1
        Assert.Equal(13, (int?)p["gridPos"]!["y"]);  // Form-Y übernommen
        Assert.Equal(6, (int?)p["gridPos"]!["w"]);
        Assert.Equal("A", (string?)p["targets"]![0]!["refId"]);
        // Thresholds: Basis-Schritt mit null + 50er-Schritt; mode absolute angelegt.
        var steps = p["fieldConfig"]!["defaults"]!["thresholds"]!["steps"] as JsonArray;
        Assert.NotNull(steps);
        Assert.Equal(2, steps!.Count);
        // Basis-Schritt ohne Wert (JSON null bzw. Property-abwesend — JsonNode kann
        // null nicht darstellen, Absenz = null in Grafana und im Heimdall-Parser).
        Assert.Null(steps[0]!["value"]);
        Assert.Equal("50", steps[1]!["value"]!.ToJsonString());
        Assert.Equal("absolute", (string?)p["fieldConfig"]!["defaults"]!["thresholds"]!["mode"]);
        Assert.Equal("area", (string?)p["options"]!["graphMode"]);
        Assert.Equal("short", (string?)p["fieldConfig"]!["defaults"]!["unit"]);
    }

    [Fact]
    public void UpsertPanel_Lehnt_Leeren_Titel_und_Fehlende_Targets_ab()
    {
        Assert.Throws<ArgumentException>(() => GrafanaDashboardEditor.UpsertPanel(RichJson, null,
            new GrafanaDashboardEditor.PanelForm("", "stat", 0, 0, 6, 4,
                new[] { new GrafanaDashboardEditor.TargetForm("up", null, false) }, null,
                Array.Empty<GrafanaDashboardEditor.ThresholdForm>(), null, null)));
        Assert.Throws<ArgumentException>(() => GrafanaDashboardEditor.UpsertPanel(RichJson, null,
            new GrafanaDashboardEditor.PanelForm("X", "stat", 0, 0, 6, 4,
                Array.Empty<GrafanaDashboardEditor.TargetForm>(), null,
                Array.Empty<GrafanaDashboardEditor.ThresholdForm>(), null, null)));
    }

    [Fact]
    public void UpsertPanel_Row_Ignoriert_Targets()
    {
        var added = GrafanaDashboardEditor.UpsertPanel(RichJson, null, new GrafanaDashboardEditor.PanelForm(
            "Sektion 2", "row", 0, 99, 24, 1, Array.Empty<GrafanaDashboardEditor.TargetForm>(), null,
            Array.Empty<GrafanaDashboardEditor.ThresholdForm>(), null, null, true));
        var root = Parse(added);
        var row = root["panels"]![2]!;
        Assert.Equal("row", (string?)row["type"]);
        Assert.Null(row["targets"]);
        Assert.Equal(24, (int?)row["gridPos"]!["w"]);
    }

    [Fact]
    public void UpsertPanel_Unbekannter_Key_Lehnt_ab()
    {
        Assert.Throws<ArgumentException>(() => GrafanaDashboardEditor.UpsertPanel(RichJson, "9",
            GrafanaDashboardEditor.ReadPanel(RichJson, "0")!));
    }

    // --- Key-Mapping inkl. rows[]-Dashboards -------------------------------------

    [Fact]
    public void Keys_Mappen_Auf_Raw_Struktur()
    {
        var entries = GrafanaDashboardEditor.ListPanels(RichJson);
        Assert.Equal(new[] { "0", "1", "1.0" }, entries.Select(e => e.Key).ToArray());
        Assert.Equal("RPS", entries[0].Title);
        Assert.Equal("Sektion", entries[1].Title);
        Assert.Equal("stat", entries[2].Type);
        Assert.Equal(1, entries[2].TargetCount);

        // rows[]-legacy-Dashboard: "1.2" erreicht rows[1].panels[2].
        const string rowsDash = """
            { "title": "Legacy", "rows": [
                { "title": "R1", "panels": [ { "type": "stat", "title": "A", "gridPos": {"x":0,"y":0,"w":6,"h":4}, "targets": [{"expr":"up"}] } ] },
                { "title": "R2", "panels": [
                    { "type": "stat", "title": "B", "gridPos": {"x":0,"y":0,"w":6,"h":4}, "targets": [{"expr":"up"}] },
                    { "type": "stat", "title": "C", "gridPos": {"x":6,"y":0,"w":6,"h":4}, "targets": [{"expr":"up"}] },
                    { "type": "timeseries", "title": "D", "gridPos": {"x":12,"y":0,"w":12,"h":8}, "targets": [{"expr":"up"}] }
                ] }
            ] }
            """;
        var legacy = GrafanaDashboardEditor.ListPanels(rowsDash);
        Assert.Equal(new[] { "0.0", "1.0", "1.1", "1.2" }, legacy.Select(e => e.Key).ToArray());

        var resolved = GrafanaDashboardEditor.ReadPanel(rowsDash, "1.2")!;
        Assert.Equal("D", resolved.Title);
        var mutated = GrafanaDashboardEditor.UpsertPanel(rowsDash, "1.2", resolved with { Title = "D2" });
        Assert.Equal("D2", (string?)Parse(mutated)["rows"]![1]!["panels"]![2]!["title"]);

        var deleted = GrafanaDashboardEditor.DeletePanel(rowsDash, "1.1");
        Assert.Equal(2, (Parse(deleted)["rows"]![1]!["panels"] as JsonArray)!.Count);
    }

    // --- Variablen -----------------------------------------------------------------

    [Fact]
    public void UpsertVariable_Leegt_Templating_bei_Dashboard_ohne_Variablen_an()
    {
        const string plain = """{ "uid": "p", "title": "Plain", "panels": [] }""";
        var withVar = GrafanaDashboardEditor.UpsertVariable(plain, null,
            new GrafanaDashboardEditor.VariableForm("job", "query", "label_values(up, job)", null, true, false));
        var root = Parse(withVar);
        Assert.Equal("job", (string?)root["templating"]!["list"]![0]!["name"]);
        Assert.Equal("query", (string?)root["templating"]!["list"]![0]!["type"]);

        // Update via Key + Delete.
        var updated = GrafanaDashboardEditor.UpsertVariable(withVar, "v0",
            new GrafanaDashboardEditor.VariableForm("job", "custom", "a,b", "a", false, false));
        var v = Parse(updated)["templating"]!["list"]![0]!;
        Assert.Equal("custom", (string?)v["type"]);
        Assert.Equal("a,b", (string?)v["query"]);
        var deleted = GrafanaDashboardEditor.DeleteVariable(updated, "v0");
        Assert.Empty((Parse(deleted)["templating"]!["list"] as JsonArray)!);
        // Nochmal loeschen -> Key existiert nicht mehr.
        Assert.Throws<ArgumentException>(() => GrafanaDashboardEditor.DeleteVariable(deleted, "v0"));
    }

    // --- Dashboard-Ebene -----------------------------------------------------------

    [Fact]
    public void Duplicate_Vergibt_Neue_Uid_auch_bei_Quelle_ohne_Uid_Feld()
    {
        var dup = GrafanaDashboardEditor.Duplicate(RichJson, GrafanaDashboardEditor.NewUid());
        var root = Parse(dup);
        Assert.NotEqual("rich", (string?)root["uid"]);
        Assert.Equal("Rich (Kopie)", (string?)root["title"]);
        // root.panels hat 2 Eintraege (Row-Kind-Panels liegen verschachtelt).
        Assert.Equal(2, (root["panels"] as JsonArray)!.Count);

        // Quelle OHNE uid-Feld (Fallback-Uid-Kandidat).
        const string noUid = """{ "title": "Ohne", "panels": [] }""";
        var dup2 = GrafanaDashboardEditor.Duplicate(noUid, "dneu", "Eigener Titel");
        Assert.Equal("dneu", (string?)Parse(dup2)["uid"]);
        Assert.Equal("Eigener Titel", (string?)Parse(dup2)["title"]);
    }

    [Fact]
    public void CreateNew_Skeleton_Ist_Parsebar()
    {
        var json = GrafanaDashboardEditor.CreateNew("Mein Dashboard");
        var parsed = GrafanaDashboardModel.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal("Mein Dashboard", parsed!.Title);
        Assert.StartsWith("d", parsed.Uid);
        Assert.Empty(parsed.Panels);
    }

    [Fact]
    public void NewUid_Ist_SafeName_konform_und_Eindeutig()
    {
        for (int i = 0; i < 50; i++)
        {
            var uid = GrafanaDashboardEditor.NewUid();
            Assert.Matches("^d[0-9a-z]{8}$", uid);
        }
        Assert.NotEqual(GrafanaDashboardEditor.NewUid(), GrafanaDashboardEditor.NewUid());
    }

    [Fact]
    public void Validate_und_ReplaceJson()
    {
        Assert.Null(GrafanaDashboardEditor.Validate(RichJson));
        Assert.NotNull(GrafanaDashboardEditor.Validate("{ kaputt"));
        Assert.NotNull(GrafanaDashboardEditor.Validate(""));
        Assert.Null(GrafanaDashboardEditor.Validate(GrafanaDashboardEditor.CreateNew("X")));

        // ReplaceJson erzwingt die Routen-Uid (auch wenn das JSON eine andere traegt).
        var replaced = GrafanaDashboardEditor.ReplaceJson("""{ "uid": "falsch", "title": "Neu" }""", "rich");
        Assert.Equal("rich", (string?)Parse(replaced)["uid"]);

        // Lenient: Trailing Commas + Kommentare werden akzeptiert.
        var lenient = GrafanaDashboardEditor.ReplaceJson(
            """
            { "title": "T", // Kommentar
                 "panels": [], }
            """, "lenient");
        Assert.Equal("T", (string?)Parse(lenient)["title"]);
    }

    // --- MatchRenderKeys / SuggestNewPos (Panel-Ebenen-Editing) --------------

    private static GrafanaPanel PanelOf(string title, string type, int x, int y, int w, int h) =>
        new(0, title, type, new GrafanaGridPos(x, y, w, h),
            Array.Empty<GrafanaTarget>(), null, null);

    [Fact]
    public void MatchRenderKeys_Findet_Pfad_Keys_flach_und_verschachtelt()
    {
        // Flach: RPS = Key "0"; Row wird nicht gerendert, Kind = Key "1.0".
        var parsed = GrafanaDashboardModel.Parse(RichJson)!;
        var keys = GrafanaDashboardEditor.MatchRenderKeys(RichJson, parsed.Panels);
        Assert.Equal(2, keys.Count);
        Assert.Equal("0", keys[0]);    // RPS
        Assert.Equal("1.0", keys[1]);  // Fehlerrate (Row-Kind-Panel)

        // Kein Treffer → null (Edit-Link entfällt).
        var fremd = new[] { PanelOf("Gibts nicht", "stat", 3, 3, 6, 4) };
        Assert.Null(GrafanaDashboardEditor.MatchRenderKeys(RichJson, fremd)[0]);
    }

    [Fact]
    public void MatchRenderKeys_RepeatKopien_Teilen_Den_Key()
    {
        // Zwei inhaltsgleiche Render-Slots (Repeat-Kopie desselben Panels) →
        // derselbe Pfad-Key, kein Doppel-Konsum.
        var p = PanelOf("Klon", "stat", 0, 0, 6, 4);
        var dash = """
            { "panels": [ { "type": "stat", "title": "Klon", "gridPos": {"x":0,"y":0,"w":6,"h":4},
                            "targets": [ { "expr": "up" } ] } ] }
            """;
        var keys = GrafanaDashboardEditor.MatchRenderKeys(dash, new[] { p, p, p });
        Assert.All(keys, k => Assert.Equal("0", k));
    }

    [Fact]
    public void SuggestNewPos_Findet_Obersten_Freien_Slot()
    {
        var entries = GrafanaDashboardEditor.ListPanels(RichJson)
            .Where(e => !string.Equals(e.Type, "row", StringComparison.Ordinal)).ToList();
        // RPS (0,0,12,8) + Fehlerrate (0,9,6,4): Y=0 rechts neben RPS frei (X=12).
        Assert.Equal((12, 0), GrafanaDashboardEditor.SuggestNewPos(entries, 12, 8));

        // Volle oberste Reihe → erste freie Zeile darunter.
        var voll = new[]
        {
            new GrafanaDashboardEditor.PanelEntry("0", null, "A", "stat", new GrafanaGridPos(0, 0, 12, 8), 1),
            new GrafanaDashboardEditor.PanelEntry("1", null, "B", "stat", new GrafanaGridPos(12, 0, 12, 8), 1),
        };
        Assert.Equal((0, 8), GrafanaDashboardEditor.SuggestNewPos(voll, 12, 8));

        // Leeres Grid: oben links.
        Assert.Equal((0, 0), GrafanaDashboardEditor.SuggestNewPos(Array.Empty<GrafanaDashboardEditor.PanelEntry>(), 12, 8));
    }
}