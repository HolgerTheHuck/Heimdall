#if NET10_0
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Heimdall;
using Xunit;
using static OpenTelemetry.Proto.Collector.Trace.V1.TraceService;

namespace Heimdall.Tests;

/// <summary>
/// Regression-Test für den gRPC+Auth-Bug (Stand-alone-Host über OTLP/gRPC mit
/// aktivem Auth). Die <c>HeimdallAuthMiddleware</c> muss gRPC-Requests
/// (Content-Type <c>application/grpc</c>) an den gRPC-Service durchreichen,
/// damit dieser seine eigene Auth via <c>OtlpGrpcAuth</c> (Header
/// <c>x-heimdall-key</c>) prüfen kann. Vor dem Fix fiel jeder gRPC-POST im
/// UI/Rest-Zweig der Middleware auf 401 (POST ohne Cookie/Basic), noch bevor
/// der Service erreicht wurde — der dokumentierte <c>x-heimdall-key</c>-Auth war
/// also unter <c>Auth.Enabled</c>=true für gRPC wirkungslos (alle 3 Signale
/// scheiterten, lokal wie im Docker-Container). Dieser Test sichert das
/// Zusammenspiel: Middleware-Passthrough für application/grpc UND
/// service-seitige Auth-Prüfung (OK mit Key, Unauthenticated ohne).
/// </summary>
public class OtlpGrpcAuthTests : HostBootTestBase
{
    private const string ApiKey = "grpc-auth-key-789";
    private const string Password = "grpc-ui-pw-012";

    public OtlpGrpcAuthTests()
    {
        // Basis setzt Auth.Enabled=false — hier überschreiben (vor lazily Host-Boot).
        SetEnv("Heimdall__Auth__Enabled", "true");
        SetEnv("Heimdall__Auth__ApiKey", ApiKey);
        SetEnv("Heimdall__Auth__Password", Password);
    }

    [Fact]
    public async Task Grpc_Mit_Korrektem_ApiKey_Liefert_OK_und_Landet_Im_Sink()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpClient = Client });
        var client = new TraceServiceClient(channel);

        var headers = new Metadata { { "x-heimdall-key", ApiKey } };
        var resp = await client.ExportAsync(BuildTraceRequest("grpc-auth-ok-span"), headers);

        Assert.NotNull(resp);
        await channel.ShutdownAsync();
        Assert.Equal(1, Query.CountSpans());
    }

    [Fact]
    public async Task Grpc_Ohne_ApiKey_Liefert_Unauthenticated()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpClient = Client });
        var client = new TraceServiceClient(channel);

        // Bewusst KEIN x-heimdall-key-Header — der Service muss Unauthenticated
        // werfen (und darf nicht vorher von der HTTP-Middleware als 401/302
        // abgefangen werden, was den Service nie erreichen ließe).
        var ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.ExportAsync(BuildTraceRequest("grpc-auth-deny-span")));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        await channel.ShutdownAsync();

        // Nicht authentifizierter Export darf nichts im Sink hinterlassen.
        Assert.Equal(0, Query.CountSpans());
    }

    [Fact]
    public async Task Grpc_Mit_Falschem_ApiKey_Liefert_Unauthenticated()
    {
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpClient = Client });
        var client = new TraceServiceClient(channel);

        var headers = new Metadata { { "x-heimdall-key", "wrong-key" } };
        var ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.ExportAsync(BuildTraceRequest("grpc-auth-wrong-span"), headers));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        await channel.ShutdownAsync();
        Assert.Equal(0, Query.CountSpans());
    }
}
#endif