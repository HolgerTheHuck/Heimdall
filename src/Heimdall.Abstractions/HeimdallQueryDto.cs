using System.Collections.Generic;

namespace Heimdall;

// ---------------------------------------------------------------------------
// Read-Vertrag (IHeimdallQuery) + storage-agnostische DTOs.
// Backends (Walhalla, SQLite) implementieren IHeimdallQuery; UI/Api/Direct
// hängen nur daran und sind so backend-neutral.
// ---------------------------------------------------------------------------

/// <summary>Eine Trace-Gruppe (Aggregation ueber trace_id).</summary>
public sealed record TraceSummary(
    string TraceId,
    long FirstStartUnixNano,
    long LastEndUnixNano,
    long DurationNs,        // Letzter End - Erster Start (Wall-Clock)
    int SpanCount,
    bool HasError);

/// <summary>Ein flacher Span (rohe Zeile); der Baum wird daraus in app gebaut.</summary>
public sealed record SpanRow(
    string TraceId,
    string SpanId,
    string ParentSpanId,
    string Name,
    int Kind,
    long StartUnixNano,
    long EndUnixNano,
    long DurationNs,
    int StatusCode,
    string? StatusMessage,
    string AttrsJson,
    string EventsJson,
    string ResourceJson,
    string? ScopeName);

/// <summary>Ein Log-Eintrag (rohe Zeile).</summary>
public sealed record LogRow(
    long TimeUnixNano,
    string? TraceId,
    string? SpanId,
    int Severity,
    string? SeverityText,
    string? Body,
    string AttrsJson,
    string? ScopeName);

/// <summary>Ein Metrik-Punkt (rohe Zeile).</summary>
public sealed record MetricRow(
    string Name,
    string? Unit,
    int Type,
    int Temporality,
    long TimeUnixNano,
    double Value,
    long? Count,
    double? Sum,
    double? Min,
    double? Max,
    string? BucketCountsJson,
    string? ExplicitBoundsJson,
    string AttrsJson);

/// <summary>Filter fuer Trace-Auflistung.
/// <c>Sort</c>/<c>Dir</c> steuern die serverseitige Sortierung VOR dem
/// Paging (damit z. B. die Langläufer-Top über alle Seiten sichtbar werden,
/// nicht nur innerhalb einer Seite). <c>Sort</c> ∈ start/duration/spans/status
/// (Allowlist im SQLite-Backend), <c>Dir</c> ∈ asc/desc; null = bisher
/// (neueste zuerst, first_start DESC). <c>ServiceName</c>/<c>ServiceVersion</c>
/// greifen auf die Resource-Attribute zu (Paar-Semantik auf demselben Span);
/// der SQLite-Backend filtert sie indexgestuetzt exakt (kein Substring).</summary>
public sealed record TraceFilter(
    long? FromUnixNano = null,
    long? ToUnixNano = null,
    bool? HasError = null,
    string? ServiceName = null,
    string? ServiceVersion = null,
    string? NameContains = null,
    int Limit = 100,
    int Offset = 0,
    string? Sort = null,
    string? Dir = null);

/// <summary>Filter fuer die flache Span-Auflistung (z. B. Server-Spans im
/// Zeitfenster, gruppiert in der App nach Controller/Endpoint). Kind ist der
/// <see cref="HSpanKind"/>-Int (Server=1); MinStatusCode der <see cref="HStatusCode"/>-
/// Int (Error=2) fuer eine reine Fehler-Sicht. Limit/Offset begrenzen das Resultset.</summary>
public sealed record SpanFilter(
    long? FromUnixNano = null,
    long? ToUnixNano = null,
    int? Kind = null,
    int? MinStatusCode = null,
    int Limit = 5000,
    int Offset = 0);

/// <summary>Ein Attribut-Filter (Loki-Label-Matcher-Semantik) fuer die
/// strukturierte Log-Feldsuche. <c>Op</c> ist <c>=</c>/<c>!=</c> (exakt) bzw.
/// <c>=~</c>/<c>!~</c> (Regex). <c>Key</c> in OTel-Punkt-Form (<c>service.name</c>)
/// oder Loki-Unterstrich-Form (<c>service_name</c>) — beides wird gematcht
/// (Normalisierung <c>_</c> ↔ <c>.</c>). Greift auf Log-Attribute UND
/// Resource-Attribute (z. B. <c>service.name</c>) zu.</summary>
public sealed record AttrFilter(string Key, string Op, string Value);

/// <summary>Filter fuer Log-Suche (text = Freitext via FTS).
/// <c>AttrFilters</c> (optional, default null = kein Feldfilter = heutiges
/// Verhalten) erlaubt index-gestuetzte Feldsuche in den OTel-Attributen
/// (Loki/LogQL-Stream-Selector-Semantik); additiv, kein Vertragsbruch.
/// <c>ServiceName</c>/<c>ServiceVersion</c> sind dedizierte Feldfilter auf den
/// Resource-Attribute (Paar-Semantik auf derselben Log-Zeile) — das Backend
/// bildet sie intern auf <see cref="AttrFilter"/> ab, d. h. UND-verknuepft
/// mit <c>AttrFilters</c> und allem anderen.</summary>
public sealed record LogSearch(
    string? Text = null,
    int? MinSeverity = null,
    long? FromUnixNano = null,
    long? ToUnixNano = null,
    string? TraceId = null,
    IReadOnlyList<AttrFilter>? AttrFilters = null,
    string? ServiceName = null,
    string? ServiceVersion = null,
    int Limit = 200,
    int Offset = 0,
    string? Sort = null,
    string? Dir = null);

/// <summary>Leseseite; pro Backend eine Implementierung.</summary>
public interface IHeimdallQuery
{
    IReadOnlyList<TraceSummary> ListTraces(TraceFilter filter);
    IReadOnlyList<SpanRow> GetTrace(string traceId);
    IReadOnlyList<LogRow> SearchLogs(LogSearch search);
    IReadOnlyList<SpanRow> ListSpans(SpanFilter filter);
    IReadOnlyList<MetricRow> MetricSeries(string name, long? fromUnixNano, long? toUnixNano, int limit = 500);

    long CountSpans();
    long CountLogs();
    long CountMetrics();

    /// <summary>Alle distinkten Metrik-Namen (OTel-native) im Zeitfenster.
    /// Default-Implementierung leer (für Backends ohne Discovery); der SQLite-Backend
    /// bedient dies über seine vorhandene <see cref="Heimdall.IHeimdallMetricSource"/>-Logik.
    /// Additiv als Default-Interface-Method -> nicht-brechend für bestehende Implementierer.</summary>
    IReadOnlyList<string> ListMetricNames(long? fromUnixNano = null, long? toUnixNano = null)
        => System.Array.Empty<string>();

    /// <summary>Alle distinkten <c>service.name</c>-Werte (Logs UND Spans) im
    /// Zeitfenster (null = global). Default-Implementierung leer (für Backends
    /// ohne Discovery) -> additiv, nicht-brechend für bestehende Implementierer.
    /// Basis für das Service-Dropdown der UI.</summary>
    IReadOnlyList<string> ListServiceNames(long? fromUnixNano = null, long? toUnixNano = null)
        => System.Array.Empty<string>();

    /// <summary>Alle <c>service.version</c>-Werte, die für genau diesen
    /// <c>service.name</c> vorkommen (Paar-Semantik: beide Keys auf derselben
    /// Log-/Span-Zeile). Leerer/null-Service -> leere Liste. Default-Implementierung
    /// leer -> additiv, nicht-brechend. Basis für das abhängige Versions-Dropdown.</summary>
    IReadOnlyList<string> ListServiceVersions(string serviceName,
        long? fromUnixNano = null, long? toUnixNano = null)
        => System.Array.Empty<string>();
}