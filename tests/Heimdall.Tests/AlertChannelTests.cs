using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.Blazor.Alerts;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer die Alarm-Kanaele (Logger/SMTP/Webhook) und den persistenten
/// <see cref="FileAlertStateStore"/>. Webhook gegen einen echten HttpListener
/// (POST-Body-Felder), SMTP via BuildMessage ohne echten Mail-Server, Logger
/// gegen einen aufzeichnenden ILogger-Mock.
/// </summary>
public class AlertChannelTests
{
    private static AlertNotification Notify(string rule = "R", AlertState state = AlertState.Firing, string id = "r1") =>
        new(rule, AlertSignal.Metric, state, 42.0, "boom", 1_700_000_000_000L, id, "/otel");

    // === LoggerAlertChannel =================================================
    [Fact]
    public async Task LoggerChannel_SchreibtAlarmInsLog()
    {
        var log = new RecordingLogger<LoggerAlertChannel>();
        var ch = new LoggerAlertChannel(log);
        Assert.Equal("logger", ch.Name);

        await ch.SendAsync(Notify("5xx-Rate", AlertState.Firing), default);

        Assert.Single(log.Messages);
        Assert.Contains("5xx-Rate", log.Messages[0]);
        Assert.Contains("Firing", log.Messages[0]);
        Assert.Contains("/otel/alerts/r1", log.Messages[0]);
    }

    // === SmtpAlertChannel ===================================================
    [Fact]
    public async Task SmtpChannel_Unkonfiguriert_SilentSkip()
    {
        var ch = new SmtpAlertChannel(new SmtpChannelOptions(), "en", NullLogger<SmtpAlertChannel>.Instance);
        await ch.SendAsync(Notify(), default);   // Host/To leer → wirft nicht, tut nichts
    }

    [Fact]
    public void SmtpChannel_BuildMessage_EnthaeltRegelUrlUndEmpfaenger()
    {
        var opts = new SmtpChannelOptions
        {
            Host = "smtp.example.com", Port = 25, From = "heimdall@ex.com", To = "ops@ex.com, dev@ex.com",
        };
        var ch = new SmtpAlertChannel(opts, "en", NullLogger<SmtpAlertChannel>.Instance);
        var msg = ch.BuildMessage(Notify("5xx-Rate", AlertState.Firing, "abc"));
        Assert.Equal("heimdall@ex.com", msg.From!.Address);
        Assert.Equal(2, msg.To.Count);
        Assert.Equal("ops@ex.com", msg.To[0]!.Address);
        Assert.Equal("dev@ex.com", msg.To[1]!.Address);
        Assert.Contains("5xx-Rate", msg.Subject);
        Assert.Contains("Firing", msg.Subject);   // en-Label für AlertState.Firing
        Assert.True(msg.IsBodyHtml);
        Assert.Contains("5xx-Rate", msg.Body);
        Assert.Contains("/otel/alerts/abc", msg.Body);
    }

    // === WebhookAlertChannel ================================================
    [Fact]
    public async Task WebhookChannel_PostetJsonAnUrl()
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? body = null;
        var handle = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var opts = new WebhookChannelOptions { Enabled = true, Url = prefix + "hook", TimeoutSeconds = 5 };
        var ch = new WebhookAlertChannel(opts, new SimpleHttpFactory(), NullLogger<WebhookAlertChannel>.Instance);
        await ch.SendAsync(Notify("5xx-Rate", AlertState.Firing, "abc"), default);
        await handle;
        listener.Stop();

        Assert.NotNull(body);
        Assert.Contains("\"state\":\"firing\"", body);
        Assert.Contains("\"rule\":\"5xx-Rate\"", body);
        Assert.Contains("\"url\":\"/otel/alerts/abc\"", body);
        Assert.Contains("\"value\":42", body);
    }

    [Fact]
    public async Task WebhookChannel_Unkonfiguriert_SilentSkip()
    {
        var ch = new WebhookAlertChannel(new WebhookChannelOptions(), new SimpleHttpFactory(), NullLogger<WebhookAlertChannel>.Instance);
        await ch.SendAsync(Notify(), default);   // Url null → tut nichts
    }

    // === FileAlertStateStore (Persistenz) ===================================
    [Fact]
    public void StateStore_Put_Get_All_Remove_Roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "heimdall-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileAlertStateStore(dir);
            Assert.Null(store.Get("r1"));
            store.Put(new AlertEvent("r1", AlertState.Firing, 1000, 1000, 6, "m", 1000));
            Assert.Equal(AlertState.Firing, store.Get("r1")?.State);
            Assert.Single(store.All());
            store.Remove("r1");
            Assert.Null(store.Get("r1"));
            Assert.Empty(store.All());
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void StateStore_PersistiertUeberNeueInstanz()
    {
        var dir = Path.Combine(Path.GetTempPath(), "heimdall-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store1 = new FileAlertStateStore(dir);
            store1.Put(new AlertEvent("r1", AlertState.Firing, 1000, 1000, 6, "m", 1000));

            // Neue Instanz laedt die selbe Datei → Zustand ueberlebt Neustart.
            var store2 = new FileAlertStateStore(dir);
            var ev = store2.Get("r1");
            Assert.NotNull(ev);
            Assert.Equal(AlertState.Firing, ev!.State);
            Assert.Equal(6, ev.LastValue);
        }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { } }
    }

    // === Helfer =============================================================
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class SimpleHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly List<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}