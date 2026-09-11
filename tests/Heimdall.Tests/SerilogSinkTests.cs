using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Heimdall;
using Heimdall.Otlp;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.Heimdall;
using Xunit;
using OtlpRequest = OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest;

namespace Heimdall.Tests;

/// <summary>
/// Verifiziert Serilog.Sinks.Heimdall: der Sink mappt Serilog-LogEvents auf das
/// OTLP-Datenmodell und füttert dieselbe OtlpConvert-Konvertierung wie die Receiver —
/// In-proc direkt in den IHeimdallSink, HTTP als Protobuf-Post an /v1/logs.
/// </summary>
public class SerilogSinkTests
{
    private sealed class CapturingSink : IHeimdallSink
    {
        public List<HSpan> Spans = new();
        public List<HLogRecord> Logs = new();
        public List<HMetricPoint> Metrics = new();
        public void WriteSpans(IReadOnlyList<HSpan> s) { lock (Spans) Spans.AddRange(s); }
        public void WriteLogs(IReadOnlyList<HLogRecord> l) { lock (Logs) Logs.AddRange(l); }
        public void WriteMetrics(IReadOnlyList<HMetricPoint> m) { lock (Metrics) Metrics.AddRange(m); }
    }

    private static readonly MessageTemplateParser TemplateParser = new();

    private static LogEvent MakeEvent(
        LogEventLevel level,
        string template,
        Exception? ex = null,
        params LogEventProperty[] properties)
    {
        var mt = TemplateParser.Parse(template);
        return new LogEvent(DateTimeOffset.Parse("2026-09-11T12:00:00.0000000Z", System.Globalization.CultureInfo.InvariantCulture),
                            level, ex, mt, properties);
    }

    /// <summary>Serilog-Event → OtlpLogRequestBuilder → OtlpConvert → genau ein HLogRecord (der Produktionspfad bis zum Sink).</summary>
    private static HLogRecord MapOne(LogEvent e, HeimdallSinkOptions? options = null)
    {
        var request = OtlpLogRequestBuilder.Build(new[] { e }, options ?? new HeimdallSinkOptions());
        var logs = OtlpConvert.ToLogs(request);
        var record = Assert.Single(logs);
        return record;
    }

    // HAttribute ist ein struct — FirstOrDefault liefert nie null, also manuell suchen.
    private static bool HasAttr(HLogRecord r, string key)
    {
        foreach (var a in r.Attributes)
            if (a.Key == key) return true;
        return false;
    }

    private static object? AttrVal(HLogRecord r, string key)
    {
        foreach (var a in r.Attributes)
            if (a.Key == key) return a.Value;
        return null;
    }

    // --- Level- und Body-Mapping ---------------------------------------------

    [Theory]
    [InlineData(LogEventLevel.Verbose, HSeverity.Trace, "TRACE")]
    [InlineData(LogEventLevel.Debug, HSeverity.Debug, "DEBUG")]
    [InlineData(LogEventLevel.Information, HSeverity.Info, "INFO")]
    [InlineData(LogEventLevel.Warning, HSeverity.Warn, "WARN")]
    [InlineData(LogEventLevel.Error, HSeverity.Error, "ERROR")]
    [InlineData(LogEventLevel.Fatal, HSeverity.Fatal, "FATAL")]
    public void Level_Maps_To_Severity_Band_And_Text(LogEventLevel level, HSeverity expected, string expectedText)
    {
        var r = MapOne(MakeEvent(level, "Nachricht"));
        Assert.Equal(expected, r.Severity);
        Assert.Equal(expectedText, r.SeverityText);
    }

    [Fact]
    public void Timestamp_Is_Unix_Nano_Now_Not_Ticks_Overflow()
    {
        // Regression: ticks*100 ohne Epoch-Abzug overflowt ulong → Logs bei ~1970,
        // im Zeitfilter unsichtbar. Erwartet: Unix-Ns des Event-Timestamps
        // (2026-09-11T12:00:00Z), Größenordnung ~1.79e18 — nicht ein Wrap-around.
        var ts = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var r = MapOne(MakeEvent(LogEventLevel.Information, "x"));

        Assert.Equal((ts.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) * 100, r.TimeUnixNano);
    }

    [Fact]
    public void Body_Is_Rendered_Message_And_Template_Lands_As_Attribute()
    {
        var e = MakeEvent(LogEventLevel.Information, "Hello {Name}!", null,
            new LogEventProperty("Name", new ScalarValue("Welt")));
        var r = MapOne(e);

        // Serilog rendert String-Properties standardmäßig mit Anführungszeichen
        // (klassisches {Name:l}-Verhalten) — RenderMessage() ist treu, wie bei jedem Serilog-Sink.
        Assert.Equal("Hello \"Welt\"!", r.Body);
        Assert.Equal((object)"Hello {Name}!", AttrVal(r, "serilog.template"));
    }

    // --- Scalar-/Property-Mapping (inkl. dokumentierter Restverluste) --------

    [Fact]
    public void Scalar_Properties_Map_To_Otlp_Types()
    {
        var e = MakeEvent(LogEventLevel.Information, "{A} {B} {C} {D} {E}", null,
                new LogEventProperty("A", new ScalarValue(42L)),
                new LogEventProperty("B", new ScalarValue(2.5d)),
                new LogEventProperty("C", new ScalarValue(true)),
                new LogEventProperty("D", new ScalarValue("text")),
                new LogEventProperty("E", new ScalarValue(Guid.Parse("12345678-1234-1234-1234-123456789abc"))));
        var r = MapOne(e);

        Assert.Equal((object)42L, AttrVal(r, "A"));
        Assert.Equal((object)2.5d, AttrVal(r, "B"));
        Assert.Equal((object)true, AttrVal(r, "C"));
        Assert.Equal((object)"text", AttrVal(r, "D"));
        Assert.Equal((object)"12345678-1234-1234-1234-123456789abc", AttrVal(r, "E")); // Guid → String
    }

    [Fact]
    public void Decimal_Maps_To_Double_And_Ulong_Overflow_To_String()
    {
        var e = MakeEvent(LogEventLevel.Information, "{Dec} {Big} {Small}", null,
                new LogEventProperty("Dec", new ScalarValue(10.5m)),
                new LogEventProperty("Big", new ScalarValue(ulong.MaxValue)),
                new LogEventProperty("Small", new ScalarValue(7UL)));
        var r = MapOne(e);

        Assert.Equal((object)10.5d, AttrVal(r, "Dec"));                 // Restverlust dokumentiert
        Assert.Equal((object)ulong.MaxValue.ToString(), AttrVal(r, "Big")); // Overflow → String
        Assert.Equal((object)7L, AttrVal(r, "Small"));
    }

    [Fact]
    public void Null_Scalar_Property_Is_Dropped()
    {
        var e = MakeEvent(LogEventLevel.Information, "{Gone} {There}", null,
                new LogEventProperty("Gone", new ScalarValue(null)),
                new LogEventProperty("There", new ScalarValue("x")));
        var r = MapOne(e);

        Assert.False(HasAttr(r, "Gone"));
        Assert.True(HasAttr(r, "There"));
    }

    [Fact]
    public void Sequence_Property_Becomes_Canonical_Json_String()
    {
        // OtlpConvert speichert Arrays als kanonischen JSON-String — wie beim OTLP-Receiver.
        var seq = new SequenceValue(new[] { new ScalarValue(1), new ScalarValue(2) });
        var e = MakeEvent(LogEventLevel.Information, "{Items}", null,
            new LogEventProperty("Items", seq));
        var r = MapOne(e);

        var json = Assert.IsType<string>(AttrVal(r, "Items"));
        Assert.Contains("intValue", json, StringComparison.Ordinal);
        Assert.Contains("2", json, StringComparison.Ordinal);
    }

    // --- Exception → Semantic Conventions ------------------------------------

    [Fact]
    public void Exception_Maps_To_Exception_Conventions()
    {
        var ex = new InvalidOperationException("kaputt");
        var e = MakeEvent(LogEventLevel.Error, "Fehler", ex);
        var r = MapOne(e);

        Assert.Equal((object)"System.InvalidOperationException", AttrVal(r, "exception.type"));
        Assert.Equal((object)"kaputt", AttrVal(r, "exception.message"));
        Assert.Contains("InvalidOperationException", Assert.IsType<string>(AttrVal(r, "exception.stacktrace")!), StringComparison.Ordinal);
    }

    // --- SourceContext → Scope ------------------------------------------------

    [Fact]
    public void SourceContext_Becomes_Scope_And_Is_Not_Duplicated_As_Attribute()
    {
        var e = MakeEvent(LogEventLevel.Information, "{SourceContext} {X}", null,
                new LogEventProperty("SourceContext", new ScalarValue("My.App.Class")),
                new LogEventProperty("X", new ScalarValue(1)));
        var r = MapOne(e);

        Assert.Equal("My.App.Class", r.Scope?.Name);
        Assert.False(HasAttr(r, "SourceContext"));
    }

    [Fact]
    public void Without_SourceContext_There_Is_No_Scope()
    {
        var r = MapOne(MakeEvent(LogEventLevel.Information, "nackt"));
        Assert.Null(r.Scope);
    }

    // --- Resource --------------------------------------------------------------

    [Fact]
    public void Resource_Carries_Service_Name_Version_And_Extras()
    {
        var e = MakeEvent(LogEventLevel.Information, "x");
        var r = MapOne(e, new HeimdallSinkOptions
        {
            ServiceName = "shop",
            ServiceVersion = "1.2.3",
            ResourceAttributes = new Dictionary<string, object?> { ["deployment.environment"] = "rz" },
        });

        Assert.Equal((object)"shop", r.Resource!.Attributes.First(a => a.Key == "service.name").Value);
        Assert.Equal((object)"1.2.3", r.Resource.Attributes.First(a => a.Key == "service.version").Value);
        Assert.Equal((object)"rz", r.Resource.Attributes.First(a => a.Key == "deployment.environment").Value);
    }

    // --- End-to-End In-proc: Logger → Batching → IHeimdallSink ------------------

    [Fact]
    public void End_To_End_InProc_Flushes_Into_Heimdall_Sink()
    {
        var capturing = new CapturingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Heimdall(capturing, new HeimdallSinkOptions
            {
                ServiceName = "e2e",
                BufferingTimeLimit = TimeSpan.FromSeconds(10), // bewusst lang → Flush muss über Dispose kommen
            })
            .CreateLogger();

        logger.Information("Bestellung {OrderId} erfasst", 4711);
        logger.Dispose(); // graceful Flush des nativen Serilog-Batchings

        var log = Assert.Single(capturing.Logs);
        Assert.Equal(HSeverity.Info, log.Severity);
        Assert.Equal("Bestellung 4711 erfasst", log.Body);
        Assert.Equal((object)"e2e", log.Resource!.Attributes.First(a => a.Key == "service.name").Value);
        Assert.Equal((object)4711L, AttrVal(log, "OrderId"));
    }

    // --- HTTP-Modus: Protobuf-Post an /v1/logs ----------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public byte[]? LastBody;
        public HttpStatusCode Response = HttpStatusCode.OK;

        // Send (nicht SendAsync): der Transmitter nutzt bewusst das synchrone HttpClient.Send.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(Response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public async Task Http_Mode_Posts_Parseable_Protobuf_With_Otlp_Content_Type()
    {
        var stub = new StubHandler();
        using var transmitter = new HttpLogTransmitter("http://heimdall:5198/otel/v1/logs",
            new HeimdallSinkOptions { Headers = new Dictionary<string, string> { ["X-Api-Key"] = "geheim" } },
            handler: stub);

        var e = MakeEvent(LogEventLevel.Warning, "Lager {Stock}", properties: new LogEventProperty("Stock", new ScalarValue(3)));
        var request = OtlpLogRequestBuilder.Build(new[] { e }, new HeimdallSinkOptions { ServiceName = "http-test" });
        transmitter.Transmit(request);

        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("application/x-protobuf", stub.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.True(stub.LastRequest.Headers.Contains("X-Api-Key"));

        // Gepostete Bytes sind wieder derselbe OTLP-Request (Protobuf-Roundtrip).
        var parsed = OtlpRequest.Parser.ParseFrom(stub.LastBody!);
        var rec = Assert.Single(parsed.ResourceLogs[0].ScopeLogs[0].LogRecords);
        Assert.Equal("Lager 3", rec.Body.StringValue);
        Assert.Equal((int)OpenTelemetry.Proto.Logs.V1.SeverityNumber.Warn, (int)rec.SeverityNumber);
    }

    [Fact]
    public async Task Http_Mode_Non2xx_Does_Not_Throw()
    {
        var stub = new StubHandler { Response = HttpStatusCode.InternalServerError };
        using var transmitter = new HttpLogTransmitter("http://heimdall:5198/otel/v1/logs",
            new HeimdallSinkOptions(), handler: stub);

        var request = OtlpLogRequestBuilder.Build(new[] { MakeEvent(LogEventLevel.Error, "ups") }, new HeimdallSinkOptions());
        transmitter.Transmit(request); // darf nicht werfen — Batch geht verloren, SelfLog meldet
    }

    // --- Resilienz: EmitBatch schluckt Fehler, SelfLog meldet -------------------

    [Fact]
    public async Task EmitBatch_Swallows_Transmitter_Errors_And_Logs_To_SelfLog()
    {
        var sink = new ThrowingTransmitter();
        var selfLog = new StringWriter();
        Serilog.Debugging.SelfLog.Enable(selfLog);
        try
        {
            using var batched = new HeimdallBatchedSink(sink, new HeimdallSinkOptions());
            await batched.EmitBatchAsync(new[] { MakeEvent(LogEventLevel.Information, "boom") }); // wirft nicht
        }
        finally
        {
            Serilog.Debugging.SelfLog.Disable();
        }
        Assert.Contains("Heimdall sink", selfLog.ToString());
    }

    private sealed class ThrowingTransmitter : IHeimdallLogTransmitter
    {
        public void Transmit(OtlpRequest request) => throw new IOException("Heimdall weg");
        public void Dispose() { }
    }
}