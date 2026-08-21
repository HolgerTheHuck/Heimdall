using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Logger-Kanal: schreibt den Alarm ins Konsol-Log (ILogger). Immer
// verfuegbar — gut fuer Dev/Demo (Alerts im Log sichtbar). Registriert via
// opts.LoggerEnabled.
// ---------------------------------------------------------------------------

/// <summary>Alarm-Kanal, der ins <see cref="ILogger"/> schreibt (Name "logger").</summary>
public sealed class LoggerAlertChannel : IAlertChannel
{
    private readonly ILogger<LoggerAlertChannel> _logger;

    public LoggerAlertChannel(ILogger<LoggerAlertChannel> logger) => _logger = logger;

    /// <inheritdoc />
    public string Name => "logger";

    /// <inheritdoc />
    public Task SendAsync(AlertNotification n, CancellationToken ct)
    {
        _logger.LogWarning("[ALERT {State}] {RuleName} ({Signal}): {Message} (value={Value}, rule={RuleId}, url={Url})",
            n.State, n.RuleName, n.Signal, n.Message ?? "", n.Value, n.RuleId, n.BasePath + "/alerts/" + n.RuleId);
        return Task.CompletedTask;
    }
}