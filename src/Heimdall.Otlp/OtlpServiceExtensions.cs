using Heimdall;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Otlp;

/// <summary>
/// Registriert den <see cref="IHeimdallSink"/> in der DI, sodass die OTLP-Endpunkte
/// (siehe <see cref="OtlpEndpointExtensions.MapHeimdallOtlp"/>) ihn aus dem Container
/// lösen können. Spiegel zu <c>AddHeimdallDashboard</c> (dort wird der Sink als
/// <see cref="IHeimdallQuery"/> registriert; hier als <see cref="IHeimdallSink"/>).
/// Optional <see cref="HeimdallOtlpHttpOptions"/> für die Admission-Control (C1).
/// </summary>
public static class OtlpServiceExtensions
{
    /// <summary>
    /// Registriert <paramref name="sink"/> als <see cref="IHeimdallSink"/>-Singleton
    /// sowie den <see cref="OtlpAdmissionLimiter"/> (aus <paramref name="opts"/>).
    /// </summary>
    public static IServiceCollection AddHeimdallOtlp(this IServiceCollection services, IHeimdallSink sink,
        HeimdallOtlpHttpOptions? opts = null)
    {
        if (sink is null) throw new System.ArgumentNullException(nameof(sink));
        services.AddSingleton(sink);
        services.AddSingleton(new OtlpAdmissionLimiter(opts?.MaxConcurrentRequests ?? 0));
        return services;
    }
}