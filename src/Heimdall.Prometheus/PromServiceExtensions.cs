using Heimdall;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Prometheus;

/// <summary>
/// Registriert die Prometheus-Schicht in der DI: <see cref="IHeimdallMetricSource"/>
/// (storage-agnostischer Lesevertrag), <see cref="IHeimdallQuery"/> (für RED-Ableitung
/// aus Spans, Phase 4), <see cref="MetricNameMapper"/> und die <see cref="PromEngine"/>-
/// Fassade. Die HTTP-Endpunkte (siehe <see cref="PromEndpointExtensions.MapHeimdallPrometheus"/>)
/// lösen <see cref="PromEngine"/> aus dem Container.
///
/// Beide Sinks implementieren <see cref="IHeimdallMetricSource"/> **und**
/// <see cref="IHeimdallQuery"/> → SelfHost:
/// <code>
/// builder.Services.AddHeimdallPrometheus(sink, sink);
/// </code>
/// </summary>
public static class PromServiceExtensions
{
    /// <summary>Registriert <paramref name="metricSource"/> und <paramref name="query"/>
    /// als Singletons plus RED-Provider, Composite (real+RED), <see cref="MetricNameMapper"/>
    /// und <see cref="PromEngine"/>. Ist <paramref name="query"/> gesetzt, werden RED-Metriken
    /// aus Server-Spans abgeleitet und dem Source als Composite untergelegt.</summary>
    public static IServiceCollection AddHeimdallPrometheus(this IServiceCollection services,
        IHeimdallMetricSource metricSource, IHeimdallQuery? query = null)
    {
        if (metricSource is null) throw new System.ArgumentNullException(nameof(metricSource));
        if (query is not null) services.AddSingleton(query);

        IHeimdallMetricSource effective = metricSource;
        if (query is not null)
        {
            var red = new RedMetricsProvider(query);
            effective = new CompositeMetricSource(metricSource, red);
        }

        services.AddSingleton(metricSource);
        services.AddSingleton(effective);
        services.AddSingleton<MetricNameMapper>();
        services.AddSingleton<PromEngine>();
        return services;
    }
}