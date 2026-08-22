using Heimdall;

namespace Heimdall.Otlp.Grpc;

/// <summary>
/// Optionen für den Heimdall OTLP/gRPC-Receiver. Transport-spezifische Konfiguration,
/// die der Host aus seinen eigenen Auth-Einstellungen besetzt (die Bibliothek referenziert
/// bewusst nicht den Host, sodass sie auch standalone/eingebettet nutzbar bleibt).
/// </summary>
public sealed class HeimdallOtlpGrpcOptions
{
    /// <summary>
    /// Wenn <c>true</c>, prüft jeder <c>Export</c>-Aufruf den Metadata-Header
    /// <c>x-heimdall-key</c> gegen <see cref="ApiKey"/>. OTel-SDKs setzen ihn via
    /// <c>OTEL_EXPORTER_OTLP_HEADERS="x-heimdall-key=…"</c>. Default <c>false</c>.
    /// </summary>
    public bool AuthEnabled { get; set; }

    /// <summary>Erwarteter API-Key; nur relevant wenn <see cref="AuthEnabled"/> <c>true</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Maximal gleichzeitige OTLP/gRPC-Export-Aufrufe (Admission Control, Workstream C1).
    /// Schützt den SQLite-Sink vor Last-Spitzen / fremden Exportern. Weitere Aufrufe
    /// werden sofort mit <c>StatusCode.ResourceExhausted</c> abgewiesen
    /// (Retry-freundlich). <c>0</c> = unbegrenzt. Default <c>0</c> (der Host besetzt
    /// den Betriebs-Default, z. B. 32). Alle drei Services teilen sich das eine Cap.
    /// </summary>
    public int MaxConcurrentRequests { get; set; }
}