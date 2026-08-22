#if NET10_0
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Google.Protobuf;
using Heimdall;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// C1 — Admission Control (Concurrency-Cap) auf dem OTLP/HTTP-Empfänger:
/// <c>MaxConcurrentRequests</c> drosselt parallele Export-Requests. Über das Cap
/// hinausgehende Requests werden sofort mit HTTP 429 abgewiesen (Retry-freundlich);
/// <c>0</c> = unbegrenzt. Die Lease wird bis ans Ende des Handlers gehalten (Parse +
/// SQLite-Write serialisieren auf _gate), darum streckt ein größerer Payload das
/// Konfliktfenster → 429 deterministisch.
/// </summary>
public class OtlpRateLimitTests : HostBootTestBase
{
    private const string CapKey = "Heimdall__Otlp__Http__MaxConcurrentRequests";

    // größerer Payload → jeder admittierte Request hält die Lease über einen
    // messbaren SQLite-Write (200 Spans), damit das Cap tatsächlich überläuft.
    private static ExportTraceServiceRequest BuildHeavyRequest(int spanCount)
    {
        var tid = new byte[] { 0xa1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var rs = new ResourceSpans
        {
            Resource = new Resource
            {
                Attributes = { new KeyValue { Key = "service.name",
                    Value = new AnyValue { StringValue = "rate-limit-test" } } }
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
                Name = "heavy-" + i,
                Kind = SpanKind.Server,
                StartTimeUnixNano = 1_000_000_000UL,
                EndTimeUnixNano = 1_800_000_000UL,
                Status = new Status { Code = Status.Types.StatusCode.Ok },
            });
        }
        rs.ScopeSpans.Add(ss);
        return new ExportTraceServiceRequest { ResourceSpans = { rs } };
    }

    private static ByteArrayContent ProtoContent(ExportTraceServiceRequest req)
    {
        var c = new ByteArrayContent(req.ToByteArray());
        c.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return c;
    }

    [Fact]
    public async Task Cap_Zwei_Bei_Vielen_Gleichzeitigen_Requests_Liefert_429_Und_200()
    {
        SetEnv(CapKey, "2");

        const int total = 12;
        var req = BuildHeavyRequest(200);
        var tasks = Enumerable.Range(0, total)
            .Select(_ => Client.PostAsync("/otel/v1/traces", ProtoContent(req)))
            .ToArray();
        var responses = await Task.WhenAll(tasks);
        var codes = responses.Select(r => r.StatusCode).ToArray();

        Assert.Contains(HttpStatusCode.TooManyRequests, codes);   // mind. einer abgewiesen
        Assert.Contains(HttpStatusCode.OK, codes);                 // mind. einer admittiert
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task Cap_Null_Liefert_Alle_200()
    {
        SetEnv(CapKey, "0");   // unbegrenzt

        const int total = 8;
        var req = BuildHeavyRequest(20);
        var tasks = Enumerable.Range(0, total)
            .Select(_ => Client.PostAsync("/otel/v1/traces", ProtoContent(req)))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var r in responses) r.Dispose();
    }
}
#endif