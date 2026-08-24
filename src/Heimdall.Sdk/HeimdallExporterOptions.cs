using System.Collections.Generic;
using Heimdall;

namespace Heimdall.Sdk;

/// <summary>
/// Optionen fuer den Heimdall-SDK-Exporter. Der <see cref="Sink"/> ist das
/// Schreibziel (z. B. <c>HeimdallHub</c>-Sink, <c>IngestBuffer</c> oder ein
/// Storage-Backend). <c>ServiceName</c>/<c>ServiceVersion</c> landen als
/// Resource auf jedem emittierten Record.
/// </summary>
public sealed class HeimdallExporterOptions
{
    /// <summary>Ziel-Sink. Pflichtfeld.</summary>
    public IHeimdallSink? Sink { get; set; }

    /// <summary>service.name der Resource (Default: "unknown").</summary>
    public string? ServiceName { get; set; }

    /// <summary>service.version der Resource.</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>Zusaetzliche Resource-Attribute (z. B. deployment.env).</summary>
    public IReadOnlyList<HAttribute>? ResourceAttributes { get; set; }

    /// <summary>
    /// Export-Intervall der Metrik-Reader in Millisekunden (Default 0 = SDK-Default
    /// 60 s). Für Live-Demos auf wenige Sekunden stellbar, sodass rate()-Fenster
    /// (~1 m) mehrere Punkte sehen und Histogramm-Dauer-Panels sofort füllen —
    /// ohne die Default-Kadenz für produktive Embedded-Nutzer zu ändern.
    /// </summary>
    public int MetricExportIntervalMs { get; set; }

    /// <summary>
    /// Pfad-Prefixe (z. B. <c>"/otel"</c>), deren Telemetrie der Exporter verwirft:
    /// Spans mit <c>http.route</c>/<c>http.target</c>/<c>url.path</c>/
    /// <c>aspnetmvc.route</c>-Tag-Prefix und Metrik-Punkte mit <c>http.route</c>-
    /// Tag-Prefix werden nicht in die Sink geschrieben. Verhindert, dass das
    /// Bedienen des Heimdall-UIs (oder andere eigene Pfade) als Verkehr der
    /// beobachteten App erfasst wird. <c>aspnetmvc.route</c> ist der Tag aus
    /// Heimdalls eigener Enrichment-Middleware (<see cref="Heimdall.AspNetCore.HeimdallAspNetCoreMiddleware"/>)
    /// und ein verlässlicher Fallback, falls die ASP.NET-Instrumentation
    /// <c>http.route</c> nicht setzt. Default null = nichts verworfen. Zum
    /// Untersuchen von Heimdall selbst leer lassen.
    /// </summary>
    public IReadOnlyList<string>? ExcludeRoutePrefixes { get; set; }

    /// <summary>
    /// Logger-Kategorie-Prefixe (z. B. <c>"Heimdall.Blazor.Alerts"</c>), deren Logs
    /// der Exporter verwirft, damit Heimdalls interne Diagnose-Logs (AlertEvaluator,
    /// Kanäle) nicht im Dashboard auftauchen. Präzise statt pauschal
    /// <c>"Heimdall."</c> wählen, damit App-eigene Logs unter einem
    /// <c>Heimdall.*</c>-Namespace nicht mitgefiltert werden (sicherheitshalber alle
    /// Heimdall-Logs herauswerfen → <c>"Heimdall."</c>, riskiert dann aber die
    /// App-Kollision). Default null = keine Logs verworfen.
    /// </summary>
    public IReadOnlyList<string>? ExcludeLogCategoryPrefixes { get; set; }

    internal HResource BuildResource()
    {
        var attrs = new List<HAttribute>();
        attrs.Add(new HAttribute("service.name", string.IsNullOrEmpty(ServiceName) ? "unknown" : ServiceName));
        if (!string.IsNullOrEmpty(ServiceVersion))
            attrs.Add(new HAttribute("service.version", ServiceVersion));
        if (ResourceAttributes is not null)
            foreach (var a in ResourceAttributes)
                if (!string.IsNullOrEmpty(a.Key)) attrs.Add(a);
        return new HResource(attrs);
    }
}