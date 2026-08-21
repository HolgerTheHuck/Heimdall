using System.Threading.Tasks;
using Grpc.Net.Client;
using Heimdall;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Xunit;
using static OpenTelemetry.Proto.Collector.Trace.V1.TraceService;

namespace Heimdall.Tests;

/// <summary>
/// End-to-End für den OTLP/gRPC-Receiver (Heimdall.Otlp.Grpc): ein ExportTraceServiceRequest
/// via gRPC-Client (GrpcChannel auf den TestServer-Host) → landet als Span im SQLite-Sink,
/// abfragbar über <see cref="IHeimdallQuery.CountSpans"/>. Beweist, dass der Proto-Split
/// (Heimdall.Otlp.Proto liefert die Message-Typen, Heimdall.Otlp.Grpc die Service-Stubs)
/// am lebenden Host funktioniert — Wire-Path <c>/opentelemetry.proto.collector.trace.v1.TraceService/Export</c>.
/// </summary>
public class OtlpGrpcReceiverTests : HostBootTestBase
{
    [Fact]
    public async Task GrpcTraceExport_LandetAlsSpanImSink()
    {
        // gRPC-Channel auf den TestServer (HttpClient = CreateClient). Unencrypted h2c via TestServer.
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpClient = Client });

        var client = new TraceServiceClient(channel);
        var req = BuildTraceRequest("grpc-roundtrip-span");

        var resp = await client.ExportAsync(req);
        Assert.NotNull(resp);
        await channel.ShutdownAsync();

        // SeedDemoData=false → genau dieser eine Span liegt im Sink.
        Assert.Equal(1, Query.CountSpans());
    }
}