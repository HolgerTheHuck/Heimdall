using System.IO;
using System.Text;
using Heimdall.Blazor.Alerts;
using Heimdall.Host;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Verifiziert die POCO-Konfigurationsbindung des Hosts (Sektion "Heimdall" →
/// <see cref="HeimdallHostOptions"/>) — bewusst via <c>.Get&lt;T&gt;()</c> ohne
/// <c>IOptions</c>-Maschinerie, konventionstreu zum Host-Code. Defaults, vollständiges
/// Schema und Overrides (Development-Äquivalent) sowie die null-Section-→-Fallback-Regel.
/// </summary>
public class HeimdallHostOptionsTests
{
    private static HeimdallHostOptions? Bind(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var cfg = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return cfg.GetSection("Heimdall").Get<HeimdallHostOptions>();
    }

    [Fact]
    public void Defaults_Bei_Leerer_Sektion()
    {
        // Sektion existiert, aber leer → Instanz mit Property-Defaults.
        var o = Bind("{\"Heimdall\":{}}") ?? new HeimdallHostOptions();

        Assert.Equal("sqlite", o.Storage.Backend);
        Assert.Equal("var/heimdall/otel.db", o.Storage.DataPath);
        Assert.Equal(7, o.Storage.RetentionDays);
        Assert.True(o.Storage.WalMode);
        // Retention-Defaults: Sub-Objekt da, alle Signale null (→ Fallback auf RetentionDays).
        Assert.NotNull(o.Storage.Retention);
        Assert.Null(o.Storage.Retention.TracesDays);
        Assert.Null(o.Storage.Retention.LogsDays);
        Assert.Null(o.Storage.Retention.MetricsDays);
        Assert.Equal(0, o.Storage.MaxBytes);              // 0 = unbegrenzt
        Assert.True(o.Storage.AutoVacuum);
        Assert.True(o.Storage.VacuumMigrateLegacy);
        Assert.True(o.Otlp.Http.Enabled);
        Assert.Equal("/otel", o.Otlp.Http.Prefix);
        Assert.True(o.Otlp.Grpc.Enabled);
        Assert.True(o.Prometheus.Enabled);
        Assert.True(o.Dashboard.Enabled);
        Assert.Equal("var/heimdall/dashboards", o.DashboardsStore.Dir);
        Assert.False(o.DashboardsStore.SeedExample);
        Assert.False(o.Auth.Enabled);
        Assert.False(o.SeedDemoData);
    }

    [Fact]
    public void Fehlende_Sektion_Liefert_Null_Fallback_New()
    {
        // Kein "Heimdall"-Key → GetSection.Exists()==false → .Get<T>() liefert null.
        // Der Host fängt das mit `?? new HeimdallHostOptions()` ab.
        var o = Bind("{}");
        Assert.Null(o);
        Assert.Equal("sqlite", (o ?? new HeimdallHostOptions()).Storage.Backend);
    }

    [Fact]
    public void Volles_Schema_Bindet_Sqlite_Mit_Auth()
    {
        var json = @"{
          ""Heimdall"": {
            ""Storage"": {
              ""Backend"": ""sqlite"",
              ""DataPath"": ""/data/otel.db"",
              ""RetentionDays"": 30,
              ""RetentionSweepMinutes"": 10,
              ""WalMode"": false
            },
            ""Otlp"": {
              ""Http"": { ""Enabled"": false, ""Prefix"": ""/ingest"" },
              ""Grpc"": { ""Enabled"": true, ""Url"": ""http://0.0.0.0:4317"" }
            },
            ""Prometheus"": { ""Enabled"": true, ""Prefix"": ""/prom"" },
            ""Dashboard"": { ""Enabled"": false, ""Prefix"": ""/ui"" },
            ""DashboardsStore"": { ""Dir"": ""/data/dash"", ""SeedExample"": true },
            ""Auth"": { ""Enabled"": true, ""ApiKey"": ""k-secret"", ""UiPassword"": ""pw"" },
            ""SeedDemoData"": true
          }
        }";

        var o = Bind(json);
        Assert.NotNull(o);
        Assert.Equal("sqlite", o!.Storage.Backend);
        Assert.Equal("/data/otel.db", o.Storage.DataPath);
        Assert.Equal(30, o.Storage.RetentionDays);
        Assert.Equal(10, o.Storage.RetentionSweepMinutes);
        Assert.False(o.Storage.WalMode);

        Assert.False(o.Otlp.Http.Enabled);
        Assert.Equal("/ingest", o.Otlp.Http.Prefix);
        Assert.True(o.Otlp.Grpc.Enabled);
        Assert.Equal("http://0.0.0.0:4317", o.Otlp.Grpc.Url);

        Assert.Equal("/prom", o.Prometheus.Prefix);
        Assert.False(o.Dashboard.Enabled);

        Assert.Equal("/data/dash", o.DashboardsStore.Dir);
        Assert.True(o.DashboardsStore.SeedExample);

        Assert.True(o.Auth.Enabled);
        Assert.Equal("k-secret", o.Auth.ApiKey);
        Assert.Equal("pw", o.Auth.UiPassword);
        Assert.True(o.SeedDemoData);
    }

    [Fact]
    public void Retention_Und_MaxBytes_Binden_Pro_Signal()
    {
        var json = @"{
          ""Heimdall"": {
            ""Storage"": {
              ""Backend"": ""sqlite"",
              ""DataPath"": ""/data/otel.db"",
              ""RetentionDays"": 7,
              ""Retention"": { ""TracesDays"": 3, ""LogsDays"": 14, ""MetricsDays"": 30 },
              ""MaxBytes"": 1073741824,
              ""RetentionSweepMinutes"": 15,
              ""WalMode"": false,
              ""AutoVacuum"": false,
              ""VacuumMigrateLegacy"": false
            }
          }
        }";

        var o = Bind(json);
        Assert.NotNull(o);
        Assert.Equal(3, o!.Storage.Retention.TracesDays);
        Assert.Equal(14, o.Storage.Retention.LogsDays);
        Assert.Equal(30, o.Storage.Retention.MetricsDays);
        Assert.Equal(7, o.Storage.RetentionDays);           // Fallback bleibt erhalten
        Assert.Equal(1073741824L, o.Storage.MaxBytes);
        Assert.Equal(15, o.Storage.RetentionSweepMinutes);
        Assert.False(o.Storage.AutoVacuum);
        Assert.False(o.Storage.VacuumMigrateLegacy);
    }

    [Fact]
    public void Development_Override_Bindet_SeedFlags()
    {
        // appsettings.Development.json-Äquivalent: nur Overrides, Rest aus Defaults.
        var json = @"{
          ""Heimdall"": {
            ""Storage"": { ""DataPath"": ""var/heimdall/otel-dev.db"" },
            ""DashboardsStore"": { ""SeedExample"": true },
            ""SeedDemoData"": true
          }
        }";

        var o = Bind(json);
        Assert.NotNull(o);
        Assert.Equal("var/heimdall/otel-dev.db", o!.Storage.DataPath);
        Assert.True(o.DashboardsStore.SeedExample);
        Assert.True(o.SeedDemoData);
        // Unüberschriebene Werte bleiben Default:
        Assert.Equal("sqlite", o.Storage.Backend);
        Assert.True(o.Dashboard.Enabled);
    }

    [Fact]
    public void Alerting_Defaults_Bei_Leerer_Sektion()
    {
        var o = Bind("{\"Heimdall\":{}}") ?? new HeimdallHostOptions();
        Assert.NotNull(o.Alerting);
        Assert.False(o.Alerting.Enabled);
        Assert.Equal(15, o.Alerting.EvaluationIntervalSeconds);
        Assert.Equal("var/heimdall/alerts/rules", o.Alerting.RulesDir);
        Assert.Equal("var/heimdall/alerts", o.Alerting.StateDir);
        Assert.True(o.Alerting.LoggerEnabled);
        Assert.False(o.Alerting.Smtp.Enabled);
        Assert.Equal(587, o.Alerting.Smtp.Port);
        Assert.True(o.Alerting.Smtp.UseTls);
        Assert.False(o.Alerting.Webhook.Enabled);
    }

    [Fact]
    public void Alerting_Volles_Schema_Bindet()
    {
        var json = @"{
          ""Heimdall"": {
            ""Alerting"": {
              ""Enabled"": true,
              ""EvaluationIntervalSeconds"": 30,
              ""RulesDir"": ""/data/alerts/rules"",
              ""StateDir"": ""/data/alerts"",
              ""Smtp"": { ""Enabled"": true, ""Host"": ""smtp.example.com"", ""Port"": 465, ""From"": ""h@x"", ""To"": ""o@x"", ""UseTls"": false, ""User"": ""u"", ""Password"": ""p"" },
              ""Webhook"": { ""Enabled"": true, ""Url"": ""https://hooks/heimdall"", ""TimeoutSeconds"": 20 },
              ""LoggerEnabled"": false
            }
          }
        }";
        var o = Bind(json);
        Assert.NotNull(o);
        Assert.True(o!.Alerting.Enabled);
        Assert.Equal(30, o.Alerting.EvaluationIntervalSeconds);
        Assert.Equal("/data/alerts/rules", o.Alerting.RulesDir);
        Assert.True(o.Alerting.Smtp.Enabled);
        Assert.Equal("smtp.example.com", o.Alerting.Smtp.Host);
        Assert.Equal(465, o.Alerting.Smtp.Port);
        Assert.False(o.Alerting.Smtp.UseTls);
        Assert.Equal("u", o.Alerting.Smtp.User);
        Assert.Equal("p", o.Alerting.Smtp.Password);
        Assert.True(o.Alerting.Webhook.Enabled);
        Assert.Equal("https://hooks/heimdall", o.Alerting.Webhook.Url);
        Assert.Equal(20, o.Alerting.Webhook.TimeoutSeconds);
        Assert.False(o.Alerting.LoggerEnabled);
    }
}