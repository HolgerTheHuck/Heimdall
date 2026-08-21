using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// SMTP-Kanal: versendet E-Mail via System.Net.Mail (Framework — kein NuGet).
// Pro Send ein eigener SmtpClient (kurzlebig, disposed). Config via
// SmtpChannelOptions. Registriert nur wenn opts.Smtp.Enabled.
// ---------------------------------------------------------------------------

/// <summary>Alarm-Kanal, der E-Mails versendet (Name "email").</summary>
public sealed class SmtpAlertChannel : IAlertChannel
{
    private readonly SmtpChannelOptions _opts;
    private readonly ILogger<SmtpAlertChannel> _logger;

    public SmtpAlertChannel(SmtpChannelOptions opts, ILogger<SmtpAlertChannel> logger)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "email";

    /// <inheritdoc />
    public Task SendAsync(AlertNotification n, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.Host) || string.IsNullOrWhiteSpace(_opts.To))
            return Task.CompletedTask;   // unvollstaendig konfiguriert → stille skip

        var msg = BuildMessage(n);
        return SendCoreAsync(msg, ct);
    }

    /// <summary>Baut die MailMessage (internal — fuer Tests ohne echten SMTP-Server).</summary>
    internal MailMessage BuildMessage(AlertNotification n)
    {
        var subject = $"[Heimdall {n.State}] {n.RuleName}";
        var url = n.BasePath + "/alerts/" + n.RuleId;
        var body =
            $"<h2>Heimdall-Alarm: {n.State}</h2>" +
            $"<p><b>Regel:</b> {Escape(n.RuleName)} <br/>" +
            $"<b>Signal:</b> {n.Signal} <br/>" +
            $"<b>Wert:</b> {n.Value:0.####} <br/>" +
            $"<b>Zeitpunkt:</b> {UnixMsToIso(n.FiredAtUnixMs)}</p>" +
            (string.IsNullOrEmpty(n.Message) ? "" : $"<p><b>Hinweis:</b> {Escape(n.Message)}</p>") +
            $"<p><a href=\"{Escape(url)}\">Im Heimdall-Dashboard ansehen</a></p>";

        var msg = new MailMessage
        {
            From = new MailAddress(_opts.From ?? "heimdall@localhost"),
            Subject = subject,
            IsBodyHtml = true,
            Body = body,
        };
        foreach (var rcpt in _opts.To!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(rcpt.Trim());
        return msg;
    }

    private async Task SendCoreAsync(MailMessage msg, CancellationToken ct)
    {
        using var client = new SmtpClient(_opts.Host, _opts.Port) { EnableSsl = _opts.UseTls };
        if (!string.IsNullOrEmpty(_opts.User))
        {
            client.Credentials = new NetworkCredential(_opts.User, _opts.Password ?? "");
        }
        try
        {
            await client.SendMailAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP-Versand fehlgeschlagen fuer Alarm {RuleName}", msg.Subject);
        }
        finally
        {
            msg.Dispose();
        }
    }

    private static string Escape(string? s) => s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") ?? "";

    private static string UnixMsToIso(long unixMs)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss zzz"); }
        catch { return unixMs.ToString(); }
    }
}