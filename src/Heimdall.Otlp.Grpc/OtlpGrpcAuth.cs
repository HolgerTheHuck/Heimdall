using System;
using System.Linq;
using Grpc.Core;

namespace Heimdall.Otlp.Grpc;

/// <summary>
/// Gemeinsamer API-Key-Check für die gRPC-Service-Implementierungen. Wirft bei
/// fehlendem/falschem Key eine <see cref="RpcException"/> mit
/// <see cref="StatusCode.Unauthenticated"/> — gRPC-Idiom statt HTTP-401.
/// </summary>
internal static class OtlpGrpcAuth
{
    public static void EnsureAuthorized(ServerCallContext context, HeimdallOtlpGrpcOptions? opts)
    {
        if (opts is null || !opts.AuthEnabled) return;
        var entry = context.RequestHeaders.FirstOrDefault(h =>
            string.Equals(h.Key, "x-heimdall-key", StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Value != opts.ApiKey)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "missing or invalid x-heimdall-key"));
        }
    }
}