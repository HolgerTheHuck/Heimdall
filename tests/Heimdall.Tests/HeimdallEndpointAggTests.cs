using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Heimdall.Blazor;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer die Controller/Endpoint-Aggregation (HeimdallEndpointAgg): Gruppenbildung
/// nach Controller/Action/Route, Auto-Dimension (Plugin-Attr vs. Route-Parsen vs.
/// Fallback), Overall = Summe, avg/min/max/p95 exakt aus Dauern, Fehlerrate aus
/// StatusCode==Error, leere Eingabe werferfrei.
/// </summary>
public class HeimdallEndpointAggTests
{
    private const long Ms = 1_000_000L; // 1 ms in ns

    // Hilfs-JSON: flaches {"k":v,...} wie die Backends es speichern.
    private static string Attrs(params (string k, string v)[] kv)
    {
        var parts = new List<string>();
        foreach (var (k, v) in kv)
            parts.Add("\"" + k + "\":\"" + v.Replace("\"", "\\\"") + "\"");
        return "{" + string.Join(",", parts) + "}";
    }

    private static Heimdall.SpanRow Span(string name, long durNs, bool error, string attrsJson)
        => new("t", "s", "", name, (int)Heimdall.HSpanKind.Server, 0, durNs, durNs,
            error ? (int)Heimdall.HStatusCode.Error : (int)Heimdall.HStatusCode.Ok,
            error ? "boom" : null, attrsJson, "[]", "{}", "api");

    // --- Gruppenbildung & Auto-Dimension -----------------------------------

    [Fact]
    public void Aggregate_Gruppiert_Nach_Controller_Und_Action()
    {
        var spans = new List<Heimdall.SpanRow>
        {
            Span("GET /api/users",     10 * Ms, false, Attrs(("http.route","/api/users"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Index"))),
            Span("GET /api/users/{id}",  8 * Ms, false, Attrs(("http.route","/api/users/{id}"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Get"))),
            Span("POST /api/orders",    25 * Ms, false, Attrs(("http.route","/api/orders"), ("aspnetmvc.controller","Orders"), ("aspnetmvc.action","Create"))),
        };

        var roll = HeimdallEndpointAgg.Aggregate(spans);

        Assert.Equal(3, roll.Overall.Count);
        Assert.Equal(2, roll.Controllers.Count);
        Assert.Contains(roll.Controllers, c => c.Controller == "Users" && c.Count == 2);
        Assert.Contains(roll.Controllers, c => c.Controller == "Orders" && c.Count == 1);

        // Endpoints unter Users: Index + Get.
        var usersEps = roll.EndpointsByController["Users"];
        Assert.Equal(2, usersEps.Count);
        Assert.Contains(usersEps, e => e.Action == "Index");
        Assert.Contains(usersEps, e => e.Action == "Get");
    }

    [Fact]
    public void Aggregate_AutoDimension_Faellt_Auf_Route_Parsen_Zurueck()
    {
        // Span mit aspnetmvc.* → Controller aus Attribut.
        // Span nur mit http.route=/cart → Route-Parsen → "cart".
        // Span ganz ohne Attribute → "(unbekannt)" + Action = Span-Name.
        var spans = new List<Heimdall.SpanRow>
        {
            Span("GET /api/users", 10 * Ms, false, Attrs(("http.route","/api/users"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Index"))),
            Span("GET /cart",       5 * Ms, false, Attrs(("http.route","/cart"))),
            Span("anonymous",       3 * Ms, false, "{}"),
        };

        var roll = HeimdallEndpointAgg.Aggregate(spans);

        Assert.Contains(roll.Controllers, c => c.Controller == "Users");
        Assert.Contains(roll.Controllers, c => c.Controller == "cart");
        Assert.Contains(roll.Controllers, c => c.Controller == "(unbekannt)");
        var unk = roll.EndpointsByController["(unbekannt)"].Single();
        Assert.Equal("anonymous", unk.Action);   // ohne Route/Action-Attr → Span-Name
    }

    [Fact]
    public void ParseControllerFromRoute_Streicht_Api_Praefix()
    {
        Assert.Equal("users",  HeimdallEndpointAgg.ParseControllerFromRoute("/api/users/{id}"));
        Assert.Equal("orders", HeimdallEndpointAgg.ParseControllerFromRoute("/api/v2/orders"));
        Assert.Equal("cart",   HeimdallEndpointAgg.ParseControllerFromRoute("/cart"));
    }

    // --- Overall = Summe, avg/min/max, p95 ---------------------------------

    [Fact]
    public void Aggregate_Overall_Ist_Summe_Aller_Gruppen()
    {
        var spans = new List<Heimdall.SpanRow>();
        // Users/Get: 1 ok + 1 Fehler
        spans.Add(Span("GET /api/users/{id}",  8 * Ms, false, Attrs(("http.route","/api/users/{id}"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Get"))));
        spans.Add(Span("GET /api/users/{id}", 12 * Ms, true,  Attrs(("http.route","/api/users/{id}"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Get"))));
        // Orders/List: 1 ok
        spans.Add(Span("GET /api/orders", 25 * Ms, false, Attrs(("http.route","/api/orders"), ("aspnetmvc.controller","Orders"), ("aspnetmvc.action","List"))));

        var roll = HeimdallEndpointAgg.Aggregate(spans);

        Assert.Equal(3, roll.Overall.Count);
        Assert.Equal(1, roll.Overall.Errors);
        // Overall avg = (8+12+25)/3 ms
        Assert.Equal((8 + 12 + 25) * Ms / 3.0, roll.Overall.AvgNs, 1);
        Assert.Equal(8 * Ms, roll.Overall.MinNs);
        Assert.Equal(25 * Ms, roll.Overall.MaxNs);
    }

    [Fact]
    public void Aggregate_Percentile_Aus_Sortierten_Dauern()
    {
        // Users/Get mit 20 Spans, Dauern 1..20 ms → sortiert [1..20] ms.
        var spans = new List<Heimdall.SpanRow>();
        for (int i = 1; i <= 20; i++)
            spans.Add(Span("GET /api/users/{id}", i * Ms, false,
                Attrs(("http.route","/api/users/{id}"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Get"))));

        var roll = HeimdallEndpointAgg.Aggregate(spans);
        var get = roll.EndpointsByController["Users"].Single();

        Assert.Equal(20, get.Count);
        Assert.Equal(1 * Ms, get.MinNs);
        Assert.Equal(20 * Ms, get.MaxNs);
        // avg = (1+..+20)/20 = 10,5 ms
        Assert.Equal(10.5 * Ms, get.AvgNs, 1);
        // p50 = pos = 21*0,50 = 10,5 → zwischen sorted[9]=10 und sorted[10]=11 → 10,5 ms
        Assert.Equal(10.5 * Ms, get.P50, 1);
        // p95 = pos = 21*0,95 = 19,95 → sorted[18]=19 + 0,95*(20-19) = 19,95 ms
        Assert.Equal(19.95 * Ms, get.P95, 1);
    }

    [Fact]
    public void Aggregate_Fehlerrate_Aus_StatusCode_Error()
    {
        var spans = new List<Heimdall.SpanRow>
        {
            Span("GET /api/users", 10 * Ms, false, Attrs(("http.route","/api/users"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Index"))),
            Span("GET /api/users", 10 * Ms, true,  Attrs(("http.route","/api/users"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Index"))),
            Span("GET /api/users", 10 * Ms, true,  Attrs(("http.route","/api/users"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Index"))),
            Span("GET /api/users", 10 * Ms, false, Attrs(("http.route","/api/users"), ("aspnetmvc.controller","Users"), ("aspnetmvc.action","Index"))),
        };

        var roll = HeimdallEndpointAgg.Aggregate(spans);
        var index = roll.EndpointsByController["Users"].Single();

        Assert.Equal(4, index.Count);
        Assert.Equal(2, index.Errors);
        Assert.Equal(0.5, (double)index.Errors / index.Count, 3);
    }

    [Fact]
    public void Aggregate_Leere_Eingabe_Ist_Werferfrei()
    {
        var roll = HeimdallEndpointAgg.Aggregate(Array.Empty<Heimdall.SpanRow>());
        Assert.Equal(0, roll.Overall.Count);
        Assert.Equal(0, roll.Overall.Errors);
        Assert.Empty(roll.Controllers);
        Assert.Equal(0, roll.Overall.AvgNs);
        Assert.Equal(0, roll.Overall.P95);
    }

    [Fact]
    public void Aggregate_Null_Eingabe_Ist_Werferfrei()
    {
        var roll = HeimdallEndpointAgg.Aggregate(null!);
        Assert.Equal(0, roll.Overall.Count);
    }
}