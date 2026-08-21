using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Google.Protobuf;
using Heimdall;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Smoke-Tests für die Stand-alone-Host-Komposition: bootet den Host (via
/// <see cref="HostBootTestBase"/>) und verifiziert Dashboard-Endpoint, OTLP/HTTP-Ingestion
/// (Protobuf), Prometheus-Buildinfo und Persistenz über denselben Sink. gRPC-Roundtrip
/// siehe <see cref="OtlpGrpcReceiverTests"/>.
/// </summary>
public class HostCompositionTests : HostBootTestBase
{
    [Fact]
    public async Task GetDashboard_Returns200()
    {
        var resp = await Client.GetAsync("/otel");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetRoot_RedirectsToDashboard()
    {
        var resp = await ClientNoRedirect.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/otel", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task PostTracesProtobuf_LandetImSink()
    {
        var req = BuildTraceRequest("host-http-span");
        var content = new ByteArrayContent(req.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var resp = await Client.PostAsync("/otel/v1/traces", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal(1, Query.CountSpans());
    }

    [Fact]
    public async Task GetBuildinfo_ReturnsSuccessJson()
    {
        var resp = await Client.GetAsync("/otel/api/v1/status/buildinfo");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"success\"", body);
    }

    [Fact]
    public void Sink_Ist_Ueber_Di_Verfuegbar()
    {
        // Der selbe Sink ist als IHeimdallSink (OTLP) UND IHeimdallQuery (UI) registriert.
        var sink = Services.GetService(typeof(IHeimdallSink));
        var query = Services.GetService(typeof(IHeimdallQuery));
        Assert.NotNull(sink);
        Assert.NotNull(query);
        Assert.Same(sink, query);
    }
}