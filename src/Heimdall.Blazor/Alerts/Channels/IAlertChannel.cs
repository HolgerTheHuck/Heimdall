using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.Blazor.Alerts;

// ---------------------------------------------------------------------------
// Kanal-Vertrag. Ein Kanal liefert eine Benachrichtigung (AlertNotification)
// an ein Ziel (Logger/E-Mail/Webhook). Neue Kanäle implementieren dieses
// Interface und werden in AddHeimdallAlerting als IAlertChannel registriert.
// Der Evaluator loest die Rule.Channels-Namen per IEnumerable<IAlertChannel>
// → ToDictionary(c => c.Name) auf.
// ---------------------------------------------------------------------------

/// <summary>Benachrichtigungskanal fuer Alarme.</summary>
public interface IAlertChannel
{
    /// <summary>Kanonischer Name (matched Rule.Channels-Eintraege, z. B. "email").</summary>
    string Name { get; }

    /// <summary>Sendet die Benachrichtigung asynchron. Darf nicht werfen (Evaluator fängt dennoch ab).</summary>
    Task SendAsync(AlertNotification notification, CancellationToken ct);
}