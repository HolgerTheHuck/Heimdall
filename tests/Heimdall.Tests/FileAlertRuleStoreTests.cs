using System;
using System.IO;
using System.Linq;
using Heimdall.Blazor.Alerts;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer den dateibasierten <see cref="FileAlertRuleStore"/>: Save/Get/List/Delete-
/// Roundtrip, Id-Generierung bei leerer Id, Robustheit gegenueber kaputten Dateien
/// und Path-Traversal-Schutz (SafeName). 1:1 an GrafanaDashboardStoreTests gespiegelt.
/// </summary>
public class FileAlertRuleStoreTests
{
    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "heimdall-alert-" + Guid.NewGuid().ToString("N"));

    private static AlertRule Sample(string id, string name) => new(
        id, name, true, AlertSignal.Log, null, "timeout", 17, null, null, null,
        300, 5, 30, new[] { "logger" }, "demo", 0);

    [Fact]
    public void Save_Get_List_Delete_Roundtrip()
    {
        var dir = NewDir();
        try
        {
            var store = new FileAlertRuleStore(dir);
            var id = store.Save(Sample("", "MyRule"));
            Assert.False(string.IsNullOrEmpty(id));

            // Datei physisch vorhanden ({id}.json).
            Assert.True(File.Exists(Path.Combine(dir, id + ".json")));

            // Get parst zurueck.
            var rule = store.Get(id);
            Assert.NotNull(rule);
            Assert.Equal("MyRule", rule!.Name);
            Assert.Equal(AlertSignal.Log, rule.Signal);
            Assert.Equal("timeout", rule.LogText);
            Assert.Equal(17, rule.MinSeverity);
            Assert.Equal(new[] { "logger" }, rule.Channels);

            // List zeigt genau einen Eintrag.
            var list = store.List();
            Assert.Single(list);
            Assert.Equal(id, list[0].Id);
            Assert.Equal("MyRule", list[0].Name);

            // Delete entfernt Datei + aus List.
            store.Delete(id);
            Assert.Null(store.Get(id));
            Assert.Empty(store.List());
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_BehaeltVorgegebeneId()
    {
        var dir = NewDir();
        try
        {
            var store = new FileAlertRuleStore(dir);
            var id = store.Save(Sample("fixed-id", "X"));
            Assert.Equal("fixed-id", id);
            Assert.True(File.Exists(Path.Combine(dir, "fixed-id.json")));
            Assert.NotNull(store.Get("fixed-id"));
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
            var store = new FileAlertRuleStore(dir);
            var goodId = store.Save(Sample("", "Good"));
            File.WriteAllText(Path.Combine(dir, "bad.json"), "gar kein json {{{");
            var list = store.List();
            Assert.Single(list);                     // nur die gueltige Regel
            Assert.Equal(goodId, list[0].Id);
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_IdMitSonderzeichen_WirdGesichert()
    {
        var dir = NewDir();
        try
        {
            var store = new FileAlertRuleStore(dir);
            store.Save(Sample("a/b\\c..d", "X"));
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Single(files);
            var name = Path.GetFileName(files[0]);
            Assert.DoesNotContain("/", name);
            Assert.DoesNotContain("\\", name);
            Assert.StartsWith(dir, Path.GetFullPath(files[0]));   // innerhalb des Verzeichnisses
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Get_Vorhanden_OhneVerzeichnis_LiefertNull()
    {
        var dir = NewDir();
        try
        {
            var store = new FileAlertRuleStore(dir);
            Assert.Null(store.Get("nicht-da"));
            Assert.Empty(store.List());              // legt Verzeichnis an, bleibt leer
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_RoundtripJSON_ErhaeltAlleFelder()
    {
        var dir = NewDir();
        try
        {
            var store = new FileAlertRuleStore(dir);
            var rule = new AlertRule("", "Voll", false, AlertSignal.Trace, null, null, null,
                true, "shop", "checkout", 60, 3, 5, new[] { "email", "webhook" }, "desc", 7);
            var id = store.Save(rule);
            var back = store.Get(id);
            Assert.NotNull(back);
            Assert.False(back!.Enabled);
            Assert.Equal(AlertSignal.Trace, back.Signal);
            Assert.True(back.HasError);
            Assert.Equal("shop", back.ServiceName);
            Assert.Equal("checkout", back.NameContains);
            Assert.Equal(60, back.WindowSeconds);
            Assert.Equal(3, back.Threshold);
            Assert.Equal(5, back.ForSeconds);
            Assert.Equal(new[] { "email", "webhook" }, back.Channels);
            Assert.Equal("desc", back.Description);
            Assert.Equal(7, back.EvalIntervalSeconds);
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }
}