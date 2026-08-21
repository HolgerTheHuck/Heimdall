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