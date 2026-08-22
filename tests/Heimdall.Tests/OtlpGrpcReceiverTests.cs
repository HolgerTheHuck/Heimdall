#if NET10_0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Heimdall;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;
using OtelStatus = OpenTelemetry.Proto.Trace.V1.Status;
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

    [Fact]
    public async Task Grpc_Admission_Cap_Liefert_ResourceExhausted_Bei_Ueberlauf()
    {
        // C1: Cap=1 — alle drei gRPC-Services teilen sich das eine Cap. Mehrere
        // gleichzeitige Heavy-Exports → mind. einer StatusCode.ResourceExhausted,
        // mind. einer erfolgreich. Der Heavy-Payload streckt das Konfliktfenster
        // (Lease wird über den SQLite-Write gehalten).
        SetEnv("Heimdall__Otlp__Grpc__MaxConcurrentRequests", "1");

        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpClient = Client });
        var client = new TraceServiceClient(channel);

        var req = BuildHeavyTraceRequest(200);
        const int total = 6;
        var tasks = new List<Task>();
        var statuses = new List<StatusCode>();
        var sync = new object();
        for (int i = 0; i < total; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await client.ExportAsync(req);
                    lock (sync) statuses.Add(StatusCode.OK);
                }
                catch (RpcException ex)
                {
                    lock (sync) statuses.Add(ex.StatusCode);
                }
            }));
        }
        await Task.WhenAll(tasks);
        await channel.ShutdownAsync();

        Assert.Contains(StatusCode.OK, statuses);
        Assert.Contains(StatusCode.ResourceExhausted, statuses);
    }

    // Größerer Payload (viele Spans) → jeder admittierte Export hält die Lease über
    // einen messbaren SQLite-Write, damit das Cap=1 deterministisch überläuft.
    private static ExportTraceServiceRequest BuildHeavyTraceRequest(int spanCount)
    {
        var tid = new byte[] { 0xa1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var rs = new ResourceSpans
        {
            Resource = new Resource
            {
                Attributes = { new KeyValue { Key = "service.name",
                    Value = new AnyValue { StringValue = "grpc-ratelimit-test" } } }
            }
        };
        var ss = new ScopeSpans { Scope = new InstrumentationScope { Name = "test", Version = "1.0" } };
        for (int i = 0; i < spanCount; i++)
        {
            var sid = new byte[8]; sid[7] = (byte)(i & 0xFF); sid[6] = (byte)((i >> 8) & 0xFF);
            ss.Spans.Add(new Span
            {
                TraceId = ByteString.CopyFrom(tid),
                SpanId = ByteString.CopyFrom(sid),
                Name = "heavy-grpc-" + i,
                Kind = SpanKind.Server,
                StartTimeUnixNano = 1_000_000_000UL,
                EndTimeUnixNano = 1_800_000_000UL,
                Status = new OtelStatus { Code = OtelStatus.Types.StatusCode.Ok },
            });
        }
        rs.ScopeSpans.Add(ss);
        return new ExportTraceServiceRequest { ResourceSpans = { rs } };
    }
}
#endif