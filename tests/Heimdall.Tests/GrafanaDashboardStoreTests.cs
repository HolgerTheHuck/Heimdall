using System;
using System.IO;
using Heimdall.Blazor.Grafana;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer den dateibasierten <see cref="FileGrafanaDashboardStore"/>:
/// Save/Get/List/Delete-Roundtrip, UID-Generierung bei fehlendem <c>uid</c>-Feld
/// und Robustheit gegenueber kaputten Dateien im Dashboard-Verzeichnis.
/// </summary>
public class GrafanaDashboardStoreTests
{
    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "heimdall-dash-" + Guid.NewGuid().ToString("N"));

    private const string DashJson = """
        {
          "uid": "demo", "title": "Demo", "schemaVersion": 39,
          "panels": [
            { "id": 1, "type": "stat", "title": "RPS", "gridPos": {"h":4,"w":4,"x":0,"y":0},
              "targets": [{"expr":"sum(rate(http_requests_total[5m]))"}] }
          ]
        }
        """;

    [Fact]
    public void Save_Get_List_Delete_Roundtrip()
    {
        var dir = NewDir();
        try
        {
            var store = new FileGrafanaDashboardStore(dir);
            var uid = store.Save(DashJson);
            Assert.Equal("demo", uid);

            // Datei physisch vorhanden.
            Assert.True(File.Exists(Path.Combine(dir, "demo.json")));

            // Get parst zurueck.
            var dash = store.Get("demo");
            Assert.NotNull(dash);
            Assert.Equal("Demo", dash!.Title);
            Assert.Single(dash.Panels);

            // GetRaw liefert das Original-JSON (pretty-printed, mit Leerzeichen).
            var raw = store.GetRaw("demo");
            Assert.Contains("\"uid\"", raw);
            Assert.Contains("\"demo\"", raw);

            // List zeigt genau einen Eintrag.
            var list = store.List();
            Assert.Single(list);
            Assert.Equal("demo", list[0].Uid);
            Assert.Equal("Demo", list[0].Title);
            Assert.Equal(1, list[0].PanelCount);

            // Delete entfernt Datei + aus List.
            store.Delete("demo");
            Assert.Null(store.Get("demo"));
            Assert.Empty(store.List());
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_OhneUid_GeneriertStabileUid()
    {
        var dir = NewDir();
        try
        {
            var store = new FileGrafanaDashboardStore(dir);
            var json = """{ "title": "Ohne UID", "panels": [] }""";
            var uid1 = store.Save(json);
            var uid2 = store.Save(json);
            Assert.False(string.IsNullOrEmpty(uid1));
            Assert.Equal(uid1, uid2);                // stabil (idempotent)
            Assert.StartsWith("d", uid1);
            Assert.NotNull(store.Get(uid1));
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void List_IgnoriertKaputteDatei()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "good.json"), DashJson);
            File.WriteAllText(Path.Combine(dir, "bad.json"), "gar kein json");
            var store = new FileGrafanaDashboardStore(dir);
            var list = store.List();
            Assert.Single(list);                     // nur das gueltige Dashboard
            Assert.Equal("demo", list[0].Uid);
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_UidMitSonderzeichen_WirdGesichert()
    {
        var dir = NewDir();
        try
        {
            var store = new FileGrafanaDashboardStore(dir);
            var json = """{ "uid": "a/b\\c..d", "title": "X", "panels": [] }""";
            var uid = store.Save(json);
            // Keine Pfad-Traversierung: '/' und '\' werden zu '_', '..' ohne
            // Separator bleibt ein harmloser Dateiname innerhalb von dir.
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Single(files);
            var name = Path.GetFileName(files[0]);
            Assert.DoesNotContain("/", name);
            Assert.DoesNotContain("\\", name);
            // Datei liegt (kanonisch) innerhalb des Dashboard-Verzeichnisses.
            Assert.StartsWith(dir, Path.GetFullPath(files[0]));
            Assert.NotNull(store.Get(uid));
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_Vorhanden_OhneVerzeichnis_LiefertNull()
    {
        var dir = NewDir();
        try
        {
            var store = new FileGrafanaDashboardStore(dir);
            Assert.Null(store.Get("nicht-da"));
            Assert.Null(store.GetRaw("nicht-da"));
            Assert.Empty(store.List());              // legt Verzeichnis an, bleibt leer
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }
}