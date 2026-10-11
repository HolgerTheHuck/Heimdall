using System.Globalization;
using System.Text.Json;
using Heimdall;

namespace Heimdall.Cli.Mcp;

// ---------------------------------------------------------------------------
// MCP-Tool-Layer: definiert die Tools (Name + JSON-Schema) und fuehrt sie gegen
// die IHeimdallQuery-Leseseite aus. Bewusst transport-agnostisch — McpServer
// (stdio) und ein spaeterer HTTP-Endpunkt („Beides") teilen sich diese Schicht.
//
// Tool-Beschreibungen sind die Agent-Schnittstelle und darum auf Englisch
// (MCP-Oekosystem-Norm); Code-Kommentare bleiben Deutsch (Projekt-Konvention).
// ---------------------------------------------------------------------------

internal sealed record Tool(string Name, string Description, Dictionary<string, object?> InputSchema);

internal static class McpTools
{
    // CamelCase fuer die MCP-Wire-Namen (name/description/inputSchema) + huebsche
    // Einrueckung fuer lesbare Ergebnisse. Ergebnis-Objekte nutzen bereits snake_case,
    // das die Policy nicht anfasst (keine Grossbuchstaben).
    internal static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private const int MaxLimit = 1000;
    private const int MaxMetricPoints = 5000;

    public static readonly IReadOnlyList<Tool> All = new Tool[]
    {
        new("heimdall.overview",
            "Overview of the Heimdall store: total spans/logs/metric points and known service names.",
            Schema()),
        new("heimdall.list_services",
            "Distinct service.name values in a time range (from logs and/or spans).",
            Schema(
                ("from", "string", "Range start — ISO-8601 timestamp or Unix epoch nanoseconds.", false),
                ("to", "string", "Range end — ISO-8601 timestamp or Unix epoch nanoseconds.", false),
                ("include_spans", "boolean", "Include trace-only services (default true).", false))),
        new("heimdall.search_logs",
            "Search log entries by freetext, minimum severity, service and time range.",
            Schema(
                ("query", "string", "Freetext (fulltext) search over log bodies.", false),
                ("severity", "string", "Minimum severity: trace|debug|info|warn|error|fatal (or numeric OTel severity).", false),
                ("service", "string", "Filter by service.name (exact).", false),
                ("service_version", "string", "Filter by service.version (exact, pairs with service on the same row).", false),
                ("from", "string", "Range start — ISO-8601 or Unix nanoseconds.", false),
                ("to", "string", "Range end — ISO-8601 or Unix nanoseconds.", false),
                ("limit", "integer", "Max rows (default 200, capped 1000).", false))),
        new("heimdall.list_traces",
            "List traces with optional service/error/time filters.",
            Schema(
                ("service", "string", "Filter by service.name (exact).", false),
                ("service_version", "string", "Filter by service.version (exact, pairs with service).", false),
                ("error_only", "boolean", "Only traces that contain an error span.", false),
                ("from", "string", "Range start — ISO-8601 or Unix nanoseconds.", false),
                ("to", "string", "Range end — ISO-8601 or Unix nanoseconds.", false),
                ("limit", "integer", "Max traces (default 100, capped 1000).", false))),
        new("heimdall.get_trace",
            "Full span list of one trace (by trace id), with per-span service and duration.",
            Schema(
                ("trace_id", "string", "The 32-char lowercase hex trace id.", true))),
        new("heimdall.list_metrics",
            "Metric names grouped by service (and scope), with optional freetext filter.",
            Schema(
                ("q", "string", "Freetext filter over metric names.", false),
                ("from", "string", "Range start — ISO-8601 or Unix nanoseconds.", false),
                ("to", "string", "Range end — ISO-8601 or Unix nanoseconds.", false))),
        new("heimdall.query_metric",
            "Time series points for one metric name.",
            Schema(
                ("name", "string", "The metric name.", true),
                ("from", "string", "Range start — ISO-8601 or Unix nanoseconds.", false),
                ("to", "string", "Range end — ISO-8601 or Unix nanoseconds.", false),
                ("limit", "integer", "Max points (default 500, capped 5000).", false))),
    };

    public static (string Text, bool IsError) Call(IHeimdallQuery q, string name, JsonElement args)
    {
        return name switch
        {
            "heimdall.overview" => Overview(q),
            "heimdall.list_services" => ListServices(q, args),
            "heimdall.search_logs" => SearchLogs(q, args),
            "heimdall.list_traces" => ListTraces(q, args),
            "heimdall.get_trace" => GetTrace(q, args),
            "heimdall.list_metrics" => ListMetrics(q, args),
            "heimdall.query_metric" => QueryMetric(q, args),
            _ => ($"Unknown tool: {name}", true),
        };
    }

    // --- Handler ----------------------------------------------------------

    private static (string, bool) Overview(IHeimdallQuery q) =>
        (Json(new
        {
            spans = q.CountSpans(),
            logs = q.CountLogs(),
            metric_points = q.CountMetrics(),
            services = q.ListServiceNames(),
            metric_name_count = q.ListMetricNames().Count,
        }), false);

    private static (string, bool) ListServices(IHeimdallQuery q, JsonElement a)
    {
        bool includeSpans = OptBool(a, "include_spans") ?? true;
        var svc = q.ListServiceNames(ParseTime(a, "from"), ParseTime(a, "to"), includeSpans);
        return (Json(new { services = svc }), false);
    }

    private static (string, bool) SearchLogs(IHeimdallQuery q, JsonElement a)
    {
        var search = new LogSearch
        {
            Text = OptString(a, "query") ?? OptString(a, "text"),
            MinSeverity = ParseSeverity(a, "severity") ?? ParseSeverity(a, "min_severity"),
            ServiceName = OptString(a, "service"),
            ServiceVersion = OptString(a, "service_version"),
            FromUnixNano = ParseTime(a, "from"),
            ToUnixNano = ParseTime(a, "to"),
            Limit = Clamp(OptInt(a, "limit") ?? 200, MaxLimit),
        };
        var rows = q.SearchLogs(search);
        var items = rows.Select(r => new
        {
            time = Iso(r.TimeUnixNano),
            time_unix_nano = r.TimeUnixNano,
            severity = r.SeverityText,
            body = r.Body,
            trace_id = r.TraceId,
            span_id = r.SpanId,
            attrs = SoftParse(r.AttrsJson),
        });
        return (Json(new { count = items.Count(), logs = items }), false);
    }

    private static (string, bool) ListTraces(IHeimdallQuery q, JsonElement a)
    {
        bool? errOnly = OptBool(a, "error_only");
        var filter = new TraceFilter
        {
            ServiceName = OptString(a, "service"),
            ServiceVersion = OptString(a, "service_version"),
            HasError = errOnly == true ? true : null,
            FromUnixNano = ParseTime(a, "from"),
            ToUnixNano = ParseTime(a, "to"),
            Limit = Clamp(OptInt(a, "limit") ?? 100, MaxLimit),
        };
        var traces = q.ListTraces(filter);
        var items = traces.Select(t => new
        {
            trace_id = t.TraceId,
            root_name = t.RootName,
            service = t.RootService,
            start = Iso(t.FirstStartUnixNano),
            duration_ms = t.DurationNs / 1_000_000.0,
            span_count = t.SpanCount,
            has_error = t.HasError,
        });
        return (Json(new { count = items.Count(), traces = items }), false);
    }

    private static (string, bool) GetTrace(IHeimdallQuery q, JsonElement a)
    {
        var tid = OptString(a, "trace_id") ?? OptString(a, "traceId");
        if (string.IsNullOrWhiteSpace(tid)) return ("Parameter \"trace_id\" is required.", true);

        var spans = q.GetTrace(tid);
        if (spans.Count == 0) return ($"Trace {tid} not found (or empty).", true);

        var items = spans.Select(s => new
        {
            span_id = s.SpanId,
            parent_span_id = s.ParentSpanId,
            name = s.Name,
            kind = ((HSpanKind)s.Kind).ToString(),
            service = ServiceOf(s.ResourceJson),
            start = Iso(s.StartUnixNano),
            duration_ms = s.DurationNs / 1_000_000.0,
            status = ((HStatusCode)s.StatusCode).ToString(),
            status_message = s.StatusMessage,
            attrs = SoftParse(s.AttrsJson),
        });
        return (Json(new { trace_id = tid, span_count = items.Count(), spans = items }), false);
    }

    private static (string, bool) ListMetrics(IHeimdallQuery q, JsonElement a)
    {
        var from = ParseTime(a, "from");
        var to = ParseTime(a, "to");
        var qs = OptString(a, "q");

        IReadOnlyList<MetricNameSummary> all = q.ListMetricSummaries(from, to);
        if (!string.IsNullOrWhiteSpace(qs))
            all = all.Where(s => s.Name.Contains(qs, StringComparison.OrdinalIgnoreCase)).ToList();

        var groups = all
            .GroupBy(s => s.ServiceName)
            .OrderBy(g => string.IsNullOrEmpty(g.Key) ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                service = string.IsNullOrEmpty(g.Key) ? "(no service)" : g.Key,
                metrics = g.OrderBy(x => x.Name, StringComparer.Ordinal)
                           .Select(x => new { name = x.Name, scope = x.ScopeName }),
            });
        return (Json(new { count = all.Count, groups }), false);
    }

    private static (string, bool) QueryMetric(IHeimdallQuery q, JsonElement a)
    {
        var name = OptString(a, "name");
        if (string.IsNullOrWhiteSpace(name)) return ("Parameter \"name\" is required.", true);

        int limit = Clamp(OptInt(a, "limit") ?? 500, MaxMetricPoints);
        var rows = q.MetricSeries(name, ParseTime(a, "from"), ParseTime(a, "to"), limit);
        if (rows.Count == 0) return ($"No points for metric \"{name}\".", true);

        var items = rows.Select(m => new
        {
            time = Iso(m.TimeUnixNano),
            time_unix_nano = m.TimeUnixNano,
            value = m.Value,
            count = m.Count,
            sum = m.Sum,
            min = m.Min,
            max = m.Max,
            attrs = SoftParse(m.AttrsJson),
        });
        var first = rows[0];
        return (Json(new
        {
            name,
            unit = first.Unit,
            type = ((HMetricType)first.Type).ToString(),
            temporality = ((HTemporality)first.Temporality).ToString(),
            point_count = items.Count(),
            points = items,
        }), false);
    }

    // --- Helpers -----------------------------------------------------------

    private static Dictionary<string, object?> Schema(params (string Name, string Type, string? Desc, bool Required)[] props)
    {
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();
        foreach (var (n, t, d, r) in props)
        {
            var p = new Dictionary<string, object?> { ["type"] = t };
            if (d is not null) p["description"] = d;
            properties[n] = p;
            if (r) required.Add(n);
        }
        var schema = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, Opts);

    private static int Clamp(int v, int max) => v < 1 ? 1 : (v > max ? max : v);

    private static string? OptString(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null => null,
            _ => el.GetRawText(),
        };
    }

    private static int? OptInt(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var sn)) return sn;
        return null;
    }

    private static bool? OptBool(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var sb)) return sb;
        return null;
    }

    /// <summary>Zeitraum-Grenze: Unix-Nanosekunden (Zahl) oder ISO-8601/String (als ns).</summary>
    private static long? ParseTime(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.GetInt64();
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (s is null) return null;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ns)) return ns;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.ToUnixTimeMilliseconds() * 1_000_000L;
        }
        return null;
    }

    private static int? ParseSeverity(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString()?.Trim().ToLowerInvariant() switch
            {
                "trace" => (int)HSeverity.Trace,
                "debug" => (int)HSeverity.Debug,
                "info" => (int)HSeverity.Info,
                "warn" or "warning" => (int)HSeverity.Warn,
                "error" => (int)HSeverity.Error,
                "fatal" => (int)HSeverity.Fatal,
                _ => null,
            };
        }
        return null;
    }

    private static string Iso(long ns) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000L)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? ServiceOf(string? resourceJson)
    {
        if (string.IsNullOrWhiteSpace(resourceJson)) return null;
        try
        {
            using var d = JsonDocument.Parse(resourceJson);
            if (d.RootElement.ValueKind == JsonValueKind.Object &&
                d.RootElement.TryGetProperty("service.name", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch { /* kaputtes resource_json ignorieren */ }
        return null;
    }

    /// <summary>JSON-Text zu einem Element machen (fuer die Ausgabe); bei Fehlern Roh-Text.</summary>
    private static object? SoftParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
        catch { return json; }
    }
}
