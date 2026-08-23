using System;
using System.Collections.Generic;
using System.Globalization;

namespace Heimdall.Blazor;

/// <summary>
/// Aggregat-Ergebnis einer Controller/Endpoint-Gruppe: Aufrufe (Load), Fehler und
/// Antwortzeit (avg/min/max + p50/p95/p99) aus den <see cref="Heimdall.SpanRow"/>.
/// DurationNs aller Spans der Gruppe fliessen ein; Perzentile werden aus den
/// sortierten Rohdauern berechnet (exakt, nicht bucket-interpoliert).
/// </summary>
public sealed record EndpointStat(
    string Controller,
    string Action,
    string Route,
    long Count,
    long Errors,
    double AvgNs,
    double MinNs,
    double MaxNs,
    double P50,
    double P95,
    double P99);

/// <summary>
/// Fensterweite Rollup der Server-Spans in drei Ebenen: Gesamt-API, Controller
/// und darunter die einzelnen Endpoints (Actions/Routen). Basis für das
/// Endpoints-Dashboard; <see cref="EndpointStat.Controller"/>/Action sind leer für
/// <see cref="Overall"/>.
/// </summary>
public sealed record EndpointRollup(
    EndpointStat Overall,
    IReadOnlyList<EndpointStat> Controllers,
    IReadOnlyDictionary<string, IReadOnlyList<EndpointStat>> EndpointsByController);

/// <summary>
/// Reine, werferfreie Aggregation der Server-Spans auf die Controller/Endpoint-
/// Hierarchie — das Gegenstück zu <see cref="HeimdallSeries"/> (das auf Metrik-
/// Punkten arbeitet). Bewert intern (via IVT für Tests sichtbar). Die Dimensionen
/// Controller/Action/Route werden <b>Auto</b> bestimmt: zuerst ein Attribut aus dem
/// Heimdall.AspNetCore-Plugin (<c>aspnetmvc.controller</c>/<c>aspnetmvc.action</c>),
/// fällt das weg, wird die Route geparst (Controller = erstes Segment nach
/// <c>api</c>/<c>api/vN</c>); fehlt auch die Route, landet der Span in
/// <c>(unbekannt)</c>. So funktioniert das Drilldown mit Plugin-Enrichment wie auch
/// mit nacktem OTel (nur <c>http.route</c>).
/// </summary>
internal static class HeimdallEndpointAgg
{
    public const string DefaultControllerAttr = "aspnetmvc.controller";
    public const string DefaultActionAttr = "aspnetmvc.action";
    public const string DefaultRouteAttr = "http.route";

    public static EndpointRollup Aggregate(
        IReadOnlyList<Heimdall.SpanRow> spans,
        string controllerAttr = DefaultControllerAttr,
        string actionAttr = DefaultActionAttr,
        string routeAttr = DefaultRouteAttr)
    {
        var byEndpoint = new Dictionary<string, Acc>();     // key = controller|action|route
        var byController = new Dictionary<string, Acc>();   // key = controller
        var overall = new Acc();
        var controllerOrder = new List<string>();

        if (spans is not null)
        {
            foreach (var s in spans)
            {
                var attrs = HeimdallCharting.ParseAttrs(s.AttrsJson);
                string? route = AttrValue(attrs, routeAttr);
                string controller = ControllerOf(attrs, controllerAttr, route);
                string action = ActionOf(attrs, actionAttr, route, s.Name);

                overall.Add(s.DurationNs, s.StatusCode);
                Acc.ForController(byController, controller, controllerOrder).Add(s.DurationNs, s.StatusCode);
                Acc.ForEndpoint(byEndpoint, controller, action, route).Add(s.DurationNs, s.StatusCode);
            }
        }

        var overallStat = overall.ToStat("", "", "");
        var controllers = new List<EndpointStat>(controllerOrder.Count);
        foreach (var c in controllerOrder)
            controllers.Add(byController[c].ToStat(c, "", ""));
        Sort(controllers);

        var endpointsByController = new Dictionary<string, IReadOnlyList<EndpointStat>>(controllerOrder.Count);
        foreach (var c in controllerOrder)
        {
            var list = new List<EndpointStat>();
            foreach (var kv in byEndpoint)
            {
                if (!kv.Key.StartsWith(c + "|", StringComparison.Ordinal)) continue;
                list.Add(kv.Value.ToStat(kv.Value.Controller!, kv.Value.Action!, kv.Value.Route!));
            }
            Sort(list);
            endpointsByController[c] = list;
        }

        return new EndpointRollup(overallStat, controllers, endpointsByController);
    }

    /// <summary>Controller-Dimension (Auto): Plugin-Attr → Route-Parsen →
    /// <c>(unbekannt)</c>.</summary>
    public static string ControllerOf(IReadOnlyList<HeimdallCharting.AttrKv> attrs, string controllerAttr, string? route)
    {
        string? c = AttrValue(attrs, controllerAttr);
        if (!string.IsNullOrWhiteSpace(c)) return c!;
        if (!string.IsNullOrWhiteSpace(route)) return ParseControllerFromRoute(route);
        return "(unbekannt)";
    }

    /// <summary>Action/Endpoint-Dimension (Auto): Plugin-Attr → Route → Span-Name.</summary>
    public static string ActionOf(IReadOnlyList<HeimdallCharting.AttrKv> attrs, string actionAttr, string? route, string spanName)
    {
        string? a = AttrValue(attrs, actionAttr);
        if (!string.IsNullOrWhiteSpace(a)) return a!;
        if (!string.IsNullOrWhiteSpace(route)) return route;
        return string.IsNullOrWhiteSpace(spanName) ? "(ohne Name)" : spanName;
    }

    /// <summary>Controller aus dem Routen-Template: führendes <c>api</c> streichern,
    /// danach ggf. ein Versions-Segment (<c>v1</c>, <c>v2</c> …); das nächste
    /// nicht-leere Segment ist der Controller. Fällt keines ab, das erste Segment.</summary>
    public static string ParseControllerFromRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "(unbekannt)";
        var segs = route.Trim('/').Split('/');
        int i = 0;
        if (i < segs.Length && string.Equals(segs[i], "api", StringComparison.OrdinalIgnoreCase)) i++;
        if (i < segs.Length && IsVersion(segs[i])) i++;   // api/v2/… → v2 überspringen
        for (; i < segs.Length; i++)
        {
            string seg = segs[i];
            if (string.IsNullOrWhiteSpace(seg)) continue;
            return seg;
        }
        // nur api-Präfixe/leer → falls überhaupt ein Segment existiert, das erste
        foreach (var seg in segs)
            if (!string.IsNullOrWhiteSpace(seg)) return seg;
        return "(unbekannt)";
    }

    private static bool IsVersion(string seg)
    {
        if (seg.Length < 2 || (seg[0] | 0x20) != 'v') return false;
        for (int i = 1; i < seg.Length; i++) if (seg[i] < '0' || seg[i] > '9') return false;
        return true;
    }

    private static string? AttrValue(IReadOnlyList<HeimdallCharting.AttrKv> attrs, string key)
    {
        if (attrs is null) return null;
        for (int i = 0; i < attrs.Count; i++)
            if (string.Equals(attrs[i].Key, key, StringComparison.Ordinal)) return attrs[i].Value;
        return null;
    }

    private static void Sort(List<EndpointStat> list)
        => list.Sort((a, b) => b.Count.CompareTo(a.Count));   // absteigend nach Aufrufen

    /// <summary>Sammelt Dauern + Status einer Gruppe und baut daraus das Stat.</summary>
    private sealed class Acc
    {
        private readonly List<double> _durations = new();
        private double _sum, _min = double.MaxValue, _max = double.MinValue;
        private long _count, _errors;
        public string? Controller, Action, Route;

        public void Add(long durationNs, int statusCode)
        {
            _count++;
            _errors += statusCode == (int)Heimdall.HStatusCode.Error ? 1 : 0;
            double d = durationNs < 0 ? 0 : durationNs;
            _durations.Add(d);
            _sum += d;
            if (d < _min) _min = d;
            if (d > _max) _max = d;
        }

        public EndpointStat ToStat(string controller, string action, string route)
        {
            double[] sorted = _durations.Count == 0 ? Array.Empty<double>() : _durations.ToArray();
            Array.Sort(sorted);
            double avg = _count > 0 ? _sum / _count : 0;
            double min = _count > 0 ? _min : 0;
            double max = _count > 0 ? _max : 0;
            double p50 = HeimdallSeries.QuantileValues(sorted, 0.50);
            double p95 = HeimdallSeries.QuantileValues(sorted, 0.95);
            double p99 = HeimdallSeries.QuantileValues(sorted, 0.99);
            return new EndpointStat(controller, action, route, _count, _errors, avg, min, max, p50, p95, p99);
        }

        public static Acc ForController(Dictionary<string, Acc> map, string key, List<string> order)
        {
            if (!map.TryGetValue(key, out var a))
            {
                a = new Acc { Controller = key };
                map[key] = a;
                order.Add(key);
            }
            return a;
        }

        public static Acc ForEndpoint(Dictionary<string, Acc> map, string controller, string action, string? route)
        {
            string key = controller + "|" + action + "|" + route;
            if (!map.TryGetValue(key, out var a))
            {
                a = new Acc { Controller = controller, Action = action, Route = route };
                map[key] = a;
            }
            return a;
        }
    }
}