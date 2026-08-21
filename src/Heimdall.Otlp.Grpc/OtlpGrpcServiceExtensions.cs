using System;
using Heimdall;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Otlp.Grpc;

/// <summary>DI-Erweiterungen für den Heimdall OTLP/gRPC-Receiver.</summary>
public static class OtlpGrpcServiceExtensions
{
    /// <summary>
    /// Registriert den <paramref name="sink"/> als <see cref="IHeimdallSink"/>-Singleton,
    /// optional die <see cref="HeimdallOtlpGrpcOptions"/> (für API-Key-Auth) und die
    /// gRPC-Service-Infrastruktur (<c>AddGrpc</c>). Die Service-Implementierungen werden
    /// per <c>MapGrpcService&lt;T&gt;</c> gemappt (siehe <see cref="OtlpGrpcEndpointExtensions"/>).
    /// </summary>
    public static IServiceCollection AddHeimdallOtlpGrpc(
        this IServiceCollection services, IHeimdallSink sink, HeimdallOtlpGrpcOptions? options = null)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        services.AddSingleton(sink);
        if (options is not null) services.AddSingleton(options);
        services.AddGrpc();
        return services;
    }
}