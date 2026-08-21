using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Controllers;
using Heimdall.AspNetCore;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer das Heimdall.AspNetCore-Enrichment: die Middleware taggt den laufenden
/// Activity (OTel-Server-Span) mit aspnetmvc.controller/action aus den MVC-Metadaten
/// des gematchten Endpunkts. Der Route-Tag-Pfad (RouteEndpoint.RoutePattern) ist im
/// Middleware-Code enthalten und kompiliert; hier wird der Controller/Action-Pfad
/// (ControllerActionDescriptor) und die no-op-Fälle getestet. Ohne Activity oder
/// ohne Endpunkt ist die Middleware no-op und wirft nicht.
/// </summary>
public class HeimdallAspNetCoreMiddlewareTests
{
    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }

    private static DefaultHttpContext NewContext() => new();

    private static Endpoint EndpointWith(ControllerActionDescriptor cad)
        => new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(cad), cad.ControllerName + "." + cad.ActionName);

    [Fact]
    public async Task Enrich_Taggt_Activity_Mit_Controller_Und_Action()
    {
        var cad = new ControllerActionDescriptor { ControllerName = "Users", ActionName = "Get" };
        var ctx = NewContext();
        ctx.Features.Set<IEndpointFeature>(new EndpointFeature { Endpoint = EndpointWith(cad) });

        var activity = new Activity("http.server.request").Start();
        try
        {
            await new HeimdallAspNetCoreMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);

            Assert.Equal("Users", activity.GetTagItem(HeimdallAspNetCoreMiddleware.ControllerTag)?.ToString());
            Assert.Equal("Get", activity.GetTagItem(HeimdallAspNetCoreMiddleware.ActionTag)?.ToString());
        }
        finally { activity.Stop(); }
    }

    [Fact]
    public async Task Enrich_Ohne_Endpoint_Ist_Noop()
    {
        var ctx = NewContext();   // kein IEndpointFeature → GetEndpoint() == null
        var activity = new Activity("http.server.request").Start();
        try
        {
            await new HeimdallAspNetCoreMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);
            Assert.Null(activity.GetTagItem(HeimdallAspNetCoreMiddleware.ControllerTag));
            Assert.Null(activity.GetTagItem(HeimdallAspNetCoreMiddleware.ActionTag));
        }
        finally { activity.Stop(); }
    }

    [Fact]
    public async Task Enrich_Ohne_Activity_Ist_Noop()
    {
        var prev = Activity.Current;
        Activity.Current = null;

        var cad = new ControllerActionDescriptor { ControllerName = "Users", ActionName = "Index" };
        var ctx = NewContext();
        ctx.Features.Set<IEndpointFeature>(new EndpointFeature { Endpoint = EndpointWith(cad) });

        try
        {
            // Darf nicht werfen, auch ohne laufenden Activity.
            await new HeimdallAspNetCoreMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);
        }
        finally { Activity.Current = prev; }
    }

    [Fact]
    public async Task Enrich_Ruft_Pipeline_Weiter()
    {
        var cad = new ControllerActionDescriptor { ControllerName = "Orders", ActionName = "List" };
        var ctx = NewContext();
        ctx.Features.Set<IEndpointFeature>(new EndpointFeature { Endpoint = EndpointWith(cad) });

        var activity = new Activity("http.server.request").Start();
        bool nextCalled = false;
        try
        {
            await new HeimdallAspNetCoreMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }).InvokeAsync(ctx);
            Assert.True(nextCalled);
        }
        finally { activity.Stop(); }
    }
}