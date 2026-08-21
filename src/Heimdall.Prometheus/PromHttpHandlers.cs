using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// Prometheus-HTTP-API-Handler. Minimal-API-Signaturen (von PromEndpointExtensions
// gebunden), delegieren an PromEngine und verpacken das Ergebnis im Prom-Envelope.
// Fehler-Map: PromQLParseException -> errorType "bad_data" (HTTP 400);
// PromQLExecException -> "execution" (HTTP 200, Prom-konform); sonst "internal" (500).
// ---------------------------------------------------------------------------

internal static class PromHttpHandlers
{
    // === /api/v1/query =====================================================
    public static IResult Query(PromEngine engine, HttpRequest req)
    {
        var query = req.Query["query"].ToString();
        if (string.IsNullOrWhiteSpace(query)) return ErrorResult("bad_data", "query is required", 400);
        long timeMs = ParseTimeMs(req.Query["time"].ToString(), null) ?? NowMs();
        try
        {
            var result = engine.EvalInstant(query, timeMs);
            return SuccessResult(PromJsonWriter.QueryResult(result));
        }
        catch (PromQLParseException ex) { return ErrorResult("bad_data", ex.Message, 400); }
        catch (PromQLExecException ex) { return ErrorResult("execution", ex.Message, 200); }
        catch (Exception ex) { return ErrorResult("internal", ex.Message, 500); }
    }

    // === /api/v1/query_range ===============================================
    public static IResult QueryRange(PromEngine engine, HttpRequest req)
    {
        var query = req.Query["query"].ToString();
        if (string.IsNullOrWhiteSpace(query)) return ErrorResult("bad_data", "query is required", 400);
        long? start = ParseTimeMs(req.Query["start"].ToString(), null);
        long? end = ParseTimeMs(req.Query["end"].ToString(), null);
        long? stepMs = ParseDurationMs(req.Query["step"].ToString());
        if (!start.HasValue || !end.HasValue || !stepMs.HasValue)
            return ErrorResult("bad_data", "start, end and step are required", 400);
        if (stepMs.Value <= 0) return ErrorResult("bad_data", "step must be positive", 400);
        try
        {
            var result = engine.EvalRange(query, start.Value, end.Value, stepMs.Value);
            return SuccessResult(PromJsonWriter.QueryResult(result));
        }
        catch (PromQLParseException ex) { return ErrorResult("bad_data", ex.Message, 400); }
        catch (PromQLExecException ex) { return ErrorResult("execution", ex.Message, 200); }
        catch (Exception ex) { return ErrorResult("internal", ex.Message, 500); }
    }

    // === /api/v1/labels ====================================================
    public static IResult Labels(PromEngine engine, HttpRequest req)
    {
        long? from = ParseTimeNs(req.Query["start"].ToString());
        long? to = ParseTimeNs(req.Query["end"].ToString());
        var names = engine.ListLabelNames(from, to);
        return SuccessResult(PromJsonWriter.StringArray(names));
    }

    // === /api/v1/label/{name}/values =======================================
    public static IResult LabelValues(PromEngine engine, HttpRequest req, string name)
    {
        long? from = ParseTimeNs(req.Query["start"].ToString());
        long? to = ParseTimeNs(req.Query["end"].ToString());
        var values = engine.ListLabelValues(name, from, to);
        return SuccessResult(PromJsonWriter.StringArray(values));
    }

    // === /api/v1/series ====================================================
    public static IResult Series(PromEngine engine, HttpRequest req)
    {
        var matchSelectors = new List<string>();
        foreach (var s in req.Query["match[]"]) if (!string.IsNullOrEmpty(s)) matchSelectors.Add(s);
        if (matchSelectors.Count == 0) return ErrorResult("bad_data", "match[] is required", 400);
        long? from = ParseTimeNs(req.Query["start"].ToString());
        long? to = ParseTimeNs(req.Query["end"].ToString());
        try
        {
            var series = engine.ListSeries(matchSelectors, from, to);
            return SuccessResult(PromJsonWriter.SeriesArray(series));
        }
        catch (PromQLParseException ex) { return ErrorResult("bad_data", ex.Message, 400); }
        catch (Exception ex) { return ErrorResult("internal", ex.Message, 500); }
    }

    // === /api/v1/metadata ==================================================
    public static IResult Metadata(PromEngine engine, HttpRequest req)
    {
        var metric = req.Query["metric"].ToString();
        string? m = string.IsNullOrWhiteSpace(metric) ? null : metric;
        var meta = engine.Metadata(m);
        return SuccessResult(PromJsonWriter.Metadata(meta));
    }

    // === /api/v1/status/buildinfo ==========================================
    public static IResult BuildInfo()
        => SuccessResult(PromJsonWriter.BuildInfoJson(PromEngine.BuildInfo()));

    // === /api/v1/status/runtimeinfo ========================================
    public static IResult RuntimeInfo()
        => SuccessResult("{\"startTime\":\"\",\"CWD\":\"\"}");

    // === /api/v1/metrics (Prom-Text-Exposition) ============================
    public static IResult Metrics(PromEngine engine)
        => Results.Text(engine.Exposition(NowMs()), "text/plain; version=0.0.4; charset=utf-8", Encoding.UTF8);

    // === Helfer ============================================================
    private static IResult SuccessResult(string dataJson)
        => Results.Text(PromJsonWriter.Success(dataJson), "application/json", Encoding.UTF8);

    private static IResult ErrorResult(string errorType, string message, int status)
        => Results.Text(PromJsonWriter.Error(errorType, message), "application/json", Encoding.UTF8, status);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Parst einen Prom-Zeitpunkt (Unix-Float-Sekunden oder RFC3339) → Unix-ms.</summary>
    internal static long? ParseTimeMs(string? s, long? defaultMs)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultMs;
        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var sec))
            return (long)(sec * 1000.0);
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.ToUnixTimeMilliseconds();
        return null;
    }

    /// <summary>Parst einen Prom-Zeitpunkt → Unix-ns (für IHeimdallMetricSource-Fenster).</summary>
    private static long? ParseTimeNs(string? s)
    {
        var ms = ParseTimeMs(s, null);
        return ms.HasValue ? ms.Value * 1_000_000L : null;
    }

    /// <summary>Parst eine Prom-Dauer (z. B. "15s", "1m", "2h", "30") → ms.</summary>
    internal static long? ParseDurationMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Nackte Zahl → Sekunden (Prom-Doku: step darf nacktzahlige Sekunden sein).
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
            return (long)(bare * 1000.0);
        return Lexer.TryParseDurationMs(s);
    }
}