using System;
using System.Net.Http;
using Heimdall;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Serilog;

/// <summary>
/// WriteTo-Erweiterungen für Heimdall. Zwei Modi:
/// <list type="bullet">
///   <item><c>WriteTo.Heimdall(sink)</c> — embedded/in-proc: direkt auf den
///     <see cref="IHeimdallSink"/> (z. B. <c>registration.Sink</c> aus
///     Heimdall.Embedded), kein Port/HTTP.</item>
///   <item><c>WriteTo.HeimdallHttp(endpoint)</c> — standalone: OTLP-Post an die
///     vollständige /v1/logs-URL eines Heimdall-Servers.</item>
/// </list>
/// Beide laufen über denselben Mapper (Serilog → OTLP) und dasselbe Batching.
/// </summary>
public static class LoggerConfigurationHeimdallExtensions
{
    /// <summary>In-proc-Sink: schreibt Events direkt in den <paramref name="sink"/> (embedded-Modus).</summary>
    public static LoggerConfiguration Heimdall(
        this LoggerSinkConfiguration loggerSinkConfiguration,
        IHeimdallSink sink,
        Serilog.Sinks.Heimdall.HeimdallSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(loggerSinkConfiguration);
        ArgumentNullException.ThrowIfNull(sink);

        var o = options ?? new Serilog.Sinks.Heimdall.HeimdallSinkOptions();
        return Register(loggerSinkConfiguration, o, new Serilog.Sinks.Heimdall.InProcLogTransmitter(sink));
    }

    /// <summary>
    /// Standalone-Sink: posted OTLP/Protobuf an <paramref name="endpoint"/> — die
    /// vollständige /v1/logs-URL eines Heimdall-Servers
    /// (z. B. http://server:5050/otel/v1/logs).
    /// </summary>
    public static LoggerConfiguration HeimdallHttp(
        this LoggerSinkConfiguration loggerSinkConfiguration,
        string endpoint,
        Serilog.Sinks.Heimdall.HeimdallSinkOptions? options = null,
        Action<HttpClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(loggerSinkConfiguration);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var o = options ?? new Serilog.Sinks.Heimdall.HeimdallSinkOptions();
        return Register(loggerSinkConfiguration, o, new Serilog.Sinks.Heimdall.HttpLogTransmitter(endpoint, o, configure));
    }

    private static LoggerConfiguration Register(
        LoggerSinkConfiguration loggerSinkConfiguration,
        Serilog.Sinks.Heimdall.HeimdallSinkOptions options,
        Serilog.Sinks.Heimdall.IHeimdallLogTransmitter transmitter)
    {
        // Serilog 4 natives Batching (kein Serilog.Sinks.PeriodicBatching nötig);
        // Dispose des Loggers flusht graceful. EagerlyEmitFirstEvent: das erste
        // Event kommt sofort an (Debugging-Weg darf nicht auf 2 s warten).
        return loggerSinkConfiguration.Sink(
            new Serilog.Sinks.Heimdall.HeimdallBatchedSink(transmitter, options),
            new BatchingOptions
            {
                BatchSizeLimit = options.BatchSizeLimit,
                BufferingTimeLimit = options.BufferingTimeLimit,
                EagerlyEmitFirstEvent = true,
            });
    }
}