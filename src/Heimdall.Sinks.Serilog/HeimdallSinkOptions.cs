using System;
using System.Collections.Generic;

namespace Serilog.Sinks.Heimdall;

/// <summary>
/// Optionen für <c>WriteTo.Heimdall</c> / <c>WriteTo.HeimdallHttp</c>
/// (<see cref="Serilog.LoggerConfigurationHeimdallExtensions"/>).
/// Die Batching-Felder steuern Serilogs natives Batching (Serilog 4);
/// die Resource-Felder landen als <c>service.*</c>-Attribute im OTLP-Resource-Block.
/// </summary>
public sealed record HeimdallSinkOptions
{
    /// <summary>service.name im Resource-Block (Default wie Heimdall.Sdk: "unknown").</summary>
    public string ServiceName { get; init; } = "unknown";

    /// <summary>Optionales service.version im Resource-Block.</summary>
    public string? ServiceVersion { get; init; }

    /// <summary>Zusätzliche Resource-Attribute (z. B. deployment.environment).</summary>
    public IReadOnlyDictionary<string, object?>? ResourceAttributes { get; init; }

    /// <summary>Maximale Event-Anzahl pro Batch (Serilog-Batching). Default 100.</summary>
    public int BatchSizeLimit { get; init; } = 100;

    /// <summary>Wie lange Events gepuffert werden, bevor sie geflusht werden (Serilog-Batching). Default 2 s.</summary>
    public TimeSpan BufferingTimeLimit { get; init; } = TimeSpan.FromSeconds(2);

    // --- Nur HTTP-Modus (WriteTo.HeimdallHttp) --------------------------------

    /// <summary>Zusätzliche HTTP-Header (z. B. Auth).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Timeout pro OTLP-HTTP-Post. Default 10 s.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
}