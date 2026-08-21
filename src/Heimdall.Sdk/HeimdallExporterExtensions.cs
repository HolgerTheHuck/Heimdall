using System.Diagnostics;
using Heimdall;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Heimdall.Sdk;

/// <summary>
/// Erweiterungen fuer die OTel-SDK-Builder: ersetzen den OTLP-Exporter durch den
/// in-process Heimdall-Exporter. Telemetrie laeuft direkt in den Heimdall-Sink,
/// ohne HTTP/gRPC und ohne Collector.
///
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.UseHeimdallExporter(sink, "my-svc"))
///     .WithMetrics(m => m.UseHeimdallExporter(sink, "my-svc"))
///     .WithLogging(l => l.UseHeimdallExporter(sink, "my-svc"));
/// </code>
/// </summary>
public static class HeimdallExporterExtensions
{
    // --- Tracing ----------------------------------------------------------

    public static TracerProviderBuilder UseHeimdallExporter(this TracerProviderBuilder builder, HeimdallExporterOptions options)
    {
        Validate(options);
        var resource = options.BuildResource();
        var exporter = new HeimdallTraceExporter(options.Sink!, resource);
        return builder.AddProcessor(new BatchActivityExportProcessor(exporter));
    }

    public static TracerProviderBuilder UseHeimdallExporter(this TracerProviderBuilder builder, IHeimdallSink sink, string? serviceName = null, string? serviceVersion = null)
        => builder.UseHeimdallExporter(new HeimdallExporterOptions { Sink = sink, ServiceName = serviceName, ServiceVersion = serviceVersion });

    // --- Metrics ----------------------------------------------------------

    public static MeterProviderBuilder UseHeimdallExporter(this MeterProviderBuilder builder, HeimdallExporterOptions options)
    {
        Validate(options);
        var resource = options.BuildResource();
        var exporter = new HeimdallMetricExporter(options.Sink!, resource);
        // 0 = SDK-Default (60 s); sonst benutzerdefinierte Kadenz (z. B. Demo 15 s).
        return options.MetricExportIntervalMs > 0
            ? builder.AddReader(new PeriodicExportingMetricReader(exporter, options.MetricExportIntervalMs))
            : builder.AddReader(new PeriodicExportingMetricReader(exporter));
    }

    public static MeterProviderBuilder UseHeimdallExporter(this MeterProviderBuilder builder, IHeimdallSink sink, string? serviceName = null, string? serviceVersion = null)
        => builder.UseHeimdallExporter(new HeimdallExporterOptions { Sink = sink, ServiceName = serviceName, ServiceVersion = serviceVersion });

    // --- Logging ----------------------------------------------------------

    public static LoggerProviderBuilder UseHeimdallExporter(this LoggerProviderBuilder builder, HeimdallExporterOptions options)
    {
        Validate(options);
        var resource = options.BuildResource();
        var exporter = new HeimdallLogExporter(options.Sink!, resource);
        return builder.AddProcessor(new BatchLogRecordExportProcessor(exporter));
    }

    public static LoggerProviderBuilder UseHeimdallExporter(this LoggerProviderBuilder builder, IHeimdallSink sink, string? serviceName = null, string? serviceVersion = null)
        => builder.UseHeimdallExporter(new HeimdallExporterOptions { Sink = sink, ServiceName = serviceName, ServiceVersion = serviceVersion });

    private static void Validate(HeimdallExporterOptions options)
    {
        if (options?.Sink is null)
            throw new System.ArgumentNullException(nameof(options), "HeimdallExporterOptions.Sink muss gesetzt sein.");
    }
}