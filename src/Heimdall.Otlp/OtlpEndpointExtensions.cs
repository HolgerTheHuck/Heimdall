using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Heimdall;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Heimdall.Otlp;

/// <summary>
/// Mappt den OTLP/HTTP-Empfänger unter ein Präfix (Default <c>/otel</c>), sodass die
/// Collector-Endpunkte <c>{prefix}/v1/traces|metrics|logs</c> entstehen. Akzeptiert
/// sowohl <c>application/x-protobuf</c> als auch <c>application/json</c> (OTLP/HTTP-
/// JSON); beide Parser landen im selben Proto-Typ → ein <see cref="OtlpConvert"/>.
/// Antwort ist eine leere <c>Export{...}ServiceResponse</c> im angeforderten Format
/// (Collector-kompatibel). Schreibt über <see cref="IHeimdallSink"/> aus DI.
///
/// Aufruf im Host:
/// <code>
/// app.MapHeimdallOtlp("/otel");   // → POST /otel/v1/traces, /v1/metrics, /v1/logs
/// </code>
/// </summary>
public static class OtlpEndpointExtensions
{
    /// <summary>
    /// Mappt <c>POST {prefix}/v1/traces|metrics|logs</c> als OTLP/HTTP-Empfänger, der
    /// eintreffende Spans/Logs/Metriken (Protobuf oder JSON) über <see cref="OtlpConvert"/>
    /// in den <see cref="IHeimdallSink"/> aus DI schreibt. <paramref name="prefix"/>
    /// ist per Default <c>/otel</c> (parallel zum Dashboard-Mount).
    /// </summary>
    public static IEndpointConventionBuilder MapHeimdallOtlp(this IEndpointRouteBuilder endpoints, string prefix = "/otel")
    {
        var group = endpoints.MapGroup(prefix);
        // Request-Size-Limit: schützt vor Memory-DoS über große Bodies (Kestrel-
        // Default 30 MB × Admission-Cap 32 ≈ GB-Peak). 10 MB pro Request deckt
        // typische OTLP-Batches; größere Exporter müssen chunken. Implementiert
        // via IRequestSizeLimitMetadata (direkt auf dem Endpoint, ohne MVC-Abhängigkeit —
        // Kestrel erkennt das Interface und setzt den Body-Limit).
        group.WithMetadata(new OtlpRequestSizeLimit(10 * 1024 * 1024));
        group.MapPost("/v1/traces", (HttpContext ctx, IHeimdallSink sink, OtlpAdmissionLimiter limiter) => TraceHandler(ctx, sink, limiter));
        group.MapPost("/v1/metrics", (HttpContext ctx, IHeimdallSink sink, OtlpAdmissionLimiter limiter) => MetricsHandler(ctx, sink, limiter));
        group.MapPost("/v1/logs", (HttpContext ctx, IHeimdallSink sink, OtlpAdmissionLimiter limiter) => LogsHandler(ctx, sink, limiter));
        return group;
    }

    /// <summary>
    /// Setzt das maximale Request-Body-Limit via <c>IRequestSizeLimitMetadata</c>
    /// (Kestrel/Minimal-API erkennen das Interface und limitieren entsprechend —
    /// ohne <c>Microsoft.AspNetCore.Mvc</c>-Abhängigkeit).
    /// </summary>
    private sealed class OtlpRequestSizeLimit : Attribute, Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
    {
        private readonly long _maxBytes;
        public OtlpRequestSizeLimit(long maxBytes) { _maxBytes = maxBytes; }
        public long? MaxRequestBodySize => _maxBytes;
    }

    private static async Task<IResult> TraceHandler(HttpContext ctx, IHeimdallSink sink, OtlpAdmissionLimiter limiter)
    {
        // Admission-Control (C1): Parsing+Write hinter dem Cap, sofort 429 bei vollem Limiter.
        if (!limiter.TryEnter(out var lease)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try
        {
            var req = await ParseAsync(ctx, OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest.Parser);
            if (req is null) return Results.BadRequest();
            try
            {
                var spans = OtlpConvert.ToSpans(req);
                if (spans.Count > 0) sink.WriteSpans(spans);
            }
            catch { return Results.BadRequest(); }
            return Respond(ctx, new OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceResponse());
        }
        finally { lease?.Dispose(); }
    }

    private static async Task<IResult> LogsHandler(HttpContext ctx, IHeimdallSink sink, OtlpAdmissionLimiter limiter)
    {
        if (!limiter.TryEnter(out var lease)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try
        {
            var req = await ParseAsync(ctx, OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest.Parser);
            if (req is null) return Results.BadRequest();
            try
            {
                var logs = OtlpConvert.ToLogs(req);
                if (logs.Count > 0) sink.WriteLogs(logs);
            }
            catch { return Results.BadRequest(); }
            return Respond(ctx, new OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceResponse());
        }
        finally { lease?.Dispose(); }
    }

    private static async Task<IResult> MetricsHandler(HttpContext ctx, IHeimdallSink sink, OtlpAdmissionLimiter limiter)
    {
        if (!limiter.TryEnter(out var lease)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try
        {
            var req = await ParseAsync(ctx, OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceRequest.Parser);
            if (req is null) return Results.BadRequest();
            try
            {
                var metrics = OtlpConvert.ToMetrics(req, out var rejected);
                if (metrics.Count > 0) sink.WriteMetrics(metrics);
                var resp = new OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceResponse();
                if (rejected > 0)
                {
                    // partial_success statt 200 OK mit still verworfenen Daten —
                    // OTLP-Spec verlangt, dass der Server rejected_data_points >= 1
                    // meldet. ExponentialHistogram/Summary werden nicht unterstützt
                    // (DESIGN §2); Legacy-Clients verlieren sonst alle Metriken ohne
                    // Signal. Granularität: Heimdall verwirft ganze Metrics, wir melden
                    // ≥ 1 je Metric (exakte DP-Zahl nicht verfügbar).
                    resp.PartialSuccess = new OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsPartialSuccess
                    {
                        RejectedDataPoints = Math.Max(1, rejected),
                        ErrorMessage = "ExponentialHistogram and Summary metrics are not supported; only Counter/Sum/Gauge/Histogram are stored.",
                    };
                }
                return Respond(ctx, resp);
            }
            catch { return Results.BadRequest(); }
        }
        finally { lease?.Dispose(); }
    }

    /// <summary>
    /// Parst den Body je nach Content-Type als Protobuf (<c>MessageParser</c>) oder
    /// JSON (<c>JsonParser</c>). Liefert null bei Lese-/Parse-Fehler → Aufrufer liefert 400.
    /// </summary>
    private static async Task<TReq?> ParseAsync<TReq>(HttpContext ctx, MessageParser<TReq> parser)
        where TReq : class, IMessage<TReq>, new()
    {
        var ct = ctx.Request.ContentType ?? string.Empty;
        try
        {
            if (ct.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
                var json = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonParser.Default.Parse<TReq>(json);
            }
            // Google.Protobuf bietet nur ParseFrom(Stream) (synchron). Der Request-Body ist
            // unter Kestrel/TestServer default synchron-IO-gesperrt (AllowSynchronousIO=false)
            // → erst async in einen MemoryStream kopieren, dann synchron parsen (MemStream-
            // Sync-IO ist erlaubt). Sonst wirft ParseFrom → catch → BadRequest (OTLP/Protobuf
            // wäre unter Default-Config unbrauchbar).
            await using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            ms.Position = 0;
            return parser.ParseFrom(ms);
        }
        catch { return null; }
    }

    /// <summary>Leere Response im angeforderten Format (Collector-kompatibel).</summary>
    private static IResult Respond(HttpContext ctx, IMessage resp)
    {
        var ct = ctx.Request.ContentType ?? string.Empty;
        if (ct.Contains("json", StringComparison.OrdinalIgnoreCase))
            return Results.Text(JsonFormatter.Default.Format(resp), "application/json", Encoding.UTF8);
        return Results.Bytes(resp.ToByteArray(), "application/x-protobuf");
    }
}