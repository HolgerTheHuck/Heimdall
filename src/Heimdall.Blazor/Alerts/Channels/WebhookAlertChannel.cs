using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Webhook-Kanal: POSTet die Alarm-Benachrichtigung als JSON an eine URL.
// Deckt Slack/Teams/PagerDuty-Webhooks ab (deren URLs als Url konfiguriert).
// Nutzt IHttpClientFactory (ueber Microsoft.AspNetCore.App-FrameworkRef).
// Registriert nur wenn opts.Webhook.Enabled.
// ---------------------------------------------------------------------------

/// <summary>Alarm-Kanal, der einen HTTP-POST-Webhook absetzt (Name "webhook").</summary>
public sealed class WebhookAlertChannel : IAlertChannel
{
    private readonly WebhookChannelOptions _opts;
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<WebhookAlertChannel> _logger;

    public WebhookAlertChannel(WebhookChannelOptions opts, IHttpClientFactory factory, ILogger<WebhookAlertChannel> logger)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "webhook";

    /// <inheritdoc />
    public async Task SendAsync(AlertNotification n, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.Url)) return;   // unvollstaendig → stille skip

        var payload = new
        {
            state = n.State.ToString().ToLowerInvariant(),
            rule = n.RuleName,
            signal = n.Signal.ToString().ToLowerInvariant(),
            value = n.Value,
            message = n.Message,
            firedAt = n.FiredAtUnixMs,
            ruleId = n.RuleId,
            url = n.BasePath + "/alerts/" + n.RuleId,
        };
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, _opts.Url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds > 0 ? _opts.TimeoutSeconds : 10));
        try
        {
            var client = _factory.CreateClient("HeimdallAlerting");
            using var resp = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook-Versand fehlgeschlagen fuer Alarm {RuleName} an {Url}", n.RuleName, _opts.Url);
        }
    }
}