namespace Heimdall.Otlp;

/// <summary>
/// Optionen für den Heimdall OTLP/HTTP-Receiver. Transport-spezifische Konfiguration,
/// die der Host aus seinen eigenen Einstellungen besetzt (die Bibliothek referenziert
/// bewusst nicht den Host, sodass sie auch standalone/eingebettet nutzbar bleibt).
/// Spiegel zu <c>HeimdallOtlpGrpcOptions</c> auf der gRPC-Seite.
/// </summary>
public sealed class HeimdallOtlpHttpOptions
{
    /// <summary>
    /// Maximal gleichzeitige OTLP/HTTP-Export-Requests (Admission Control, Workstream C1).
    /// Schützt den SQLite-Sink vor Last-Spitzen / fremden Exportern. Weitere Requests
    /// werden sofort mit HTTP 429 abgewiesen (Retry-freundlich). <c>0</c> = unbegrenzt.
    /// Default <c>0</c> (der Host besetzt den Betriebs-Default, z. B. 32).
    /// </summary>
    public int MaxConcurrentRequests { get; set; }
}