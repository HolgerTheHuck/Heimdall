using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Heimdall.Blazor;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// SMTP-Kanal: versendet E-Mail via System.Net.Mail (Framework — kein NuGet).
// Pro Send ein eigener SmtpClient (kurzlebig, disposed). Config via
// SmtpChannelOptions. Registriert nur wenn opts.Smtp.Enabled.
// Betreff/Body werden i18n-localisiert über die statische HeimdallI18n-Tabelle;
// die Sprache kommt vom AlertEvaluator (kein HttpContext) via ctor-Parameter
// (HeimdallAlertingOptions.Language), nicht vom pro-Request-Cookie.
// ---------------------------------------------------------------------------

/// <summary>Alarm-Kanal, der E-Mails versendet (Name "email").</summary>
public sealed class SmtpAlertChannel : IAlertChannel
{
    private readonly SmtpChannelOptions _opts;
    private readonly string _lang;
    private readonly ILogger<SmtpAlertChannel> _logger;

    public SmtpAlertChannel(SmtpChannelOptions opts, string lang, ILogger<SmtpAlertChannel> logger)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _lang = HeimdallI18n.IsSupported(lang) ? lang : HeimdallI18n.DefaultLang;
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
        var stateLabel = HeimdallI18n.T(_lang, StateKey(n.State));
        var signalLabel = HeimdallI18n.T(_lang, SignalKey(n.Signal));
        var subject = $"[Heimdall {stateLabel}] {n.RuleName}";
        var url = n.BasePath + "/alerts/" + n.RuleId;
        var body =
            $"<h2>{Escape(HeimdallI18n.T(_lang, "alert.mail.body.heading"))} {Escape(stateLabel)}</h2>" +
            $"<p><b>{Escape(HeimdallI18n.T(_lang, "alert.mail.body.rule"))}:</b> {Escape(n.RuleName)} <br/>" +
            $"<b>{Escape(HeimdallI18n.T(_lang, "alert.mail.body.signal"))}:</b> {Escape(signalLabel)} <br/>" +
            $"<b>{Escape(HeimdallI18n.T(_lang, "alert.mail.body.value"))}:</b> {n.Value:0.####} <br/>" +
            $"<b>{Escape(HeimdallI18n.T(_lang, "alert.mail.body.time"))}:</b> {UnixMsToIso(n.FiredAtUnixMs)}</p>" +
            (string.IsNullOrEmpty(n.Message) ? "" : $"<p><b>{Escape(HeimdallI18n.T(_lang, "alert.mail.body.note"))}:</b> {Escape(n.Message)}</p>") +
            $"<p><a href=\"{Escape(url)}\">{Escape(HeimdallI18n.T(_lang, "alert.mail.body.link"))}</a></p>";

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

    private static string StateKey(AlertState s) => s switch
    {
        AlertState.Ok => "alert.state.ok",
        AlertState.Pending => "alert.state.pending",
        AlertState.Firing => "alert.state.firing",
        AlertState.Resolved => "alert.state.resolved",
        _ => "alert.state.ok",
    };

    private static string SignalKey(AlertSignal s) => s switch
    {
        AlertSignal.Metric => "alert.signal.metric",
        AlertSignal.Log => "alert.signal.log",
        AlertSignal.Trace => "alert.signal.trace",
        _ => "alert.signal.metric",
    };

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