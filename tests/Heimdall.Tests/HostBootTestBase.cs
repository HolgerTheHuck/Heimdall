#if NET10_0
using System.Net.Http;
using Google.Protobuf;
using Heimdall;
using Heimdall.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using SpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;

namespace Heimdall.Tests;

/// <summary>
/// Basis für Host-Boot-Tests: bootet den Stand-alone-Host via
/// <see cref="WebApplicationFactory{Program}"/> auf einer einzigartigen Temp-DB (SQLite,
/// kein Demo-Seeding, Auth aus) — gesteuert über Prozess-Umgebungsvariablen, die
/// <c>Program.cs</c> via <c>builder.Configuration</c> liest. Stellt <see cref="Client"/>
/// (HTTP) und <see cref="Query"/> (Assertions-Hook) bereit. Dispose räumt Env-Vars + Temp-Dir.
/// </summary>
public abstract class HostBootTestBase : IDisposable
{
    private readonly string _dir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<string> _envKeys = new();
    private bool _disposed;

    protected HostBootTestBase()
    {
        _dir = Path.Combine(Path.GetTempPath(), "heimdall-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // Env-Vars (prozessglobal — Serialisierung via [assembly: CollectionBehavior]).
        _envKeys.AddRange(new[]
        {
            "ASPNETCORE_ENVIRONMENT",
            "Heimdall__Storage__Backend",
            "Heimdall__Storage__DataPath",
            "Heimdall__SeedDemoData",
            "Heimdall__DashboardsStore__SeedExample",
            "Heimdall__DashboardsStore__Dir",
            "Heimdall__Auth__Enabled",
        });
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        Environment.SetEnvironmentVariable("Heimdall__Storage__Backend", "sqlite");
        Environment.SetEnvironmentVariable("Heimdall__Storage__DataPath", Path.Combine(_dir, "otel.db"));
        Environment.SetEnvironmentVariable("Heimdall__SeedDemoData", "false");
        Environment.SetEnvironmentVariable("Heimdall__DashboardsStore__SeedExample", "false");
        Environment.SetEnvironmentVariable("Heimdall__DashboardsStore__Dir", Path.Combine(_dir, "dash"));
        Environment.SetEnvironmentVariable("Heimdall__Auth__Enabled", "false");

        _factory = new WebApplicationFactory<Program>();
    }

    /// <summary>
    /// Setzt eine Prozess-Umgebungsvariable (wird von <c>Program.cs</c> via Env-Provider
    /// gelesen) und registriert sie fürs Dispose-Cleanup. Host bootet lazily beim ersten
    /// <see cref="Client"/>-Zugriff — also im abgeleiteten Konstruktor VOR der ersten
    /// Nutzung aufrufen, um die Basis-Defaults (z. B. Auth aus) zu überschreiben.
    /// </summary>
    protected void SetEnv(string key, string? value)
    {
        Environment.SetEnvironmentVariable(key, value);
        if (!_envKeys.Contains(key)) _envKeys.Add(key);
    }

    /// <summary>
    /// Simuliert ein Deployment hinter einem Pfad-Prefix (IIS-Sub-Application /
    /// Reverse-Proxy mit Pfad-Strip): setzt <c>TestServer.BaseAddress</c> — dessen
    /// Pfadkomponente wird auf allen Requests als <c>Request.PathBase</c> gesetzt,
    /// exakt wie das ASP.NET Core Module es bei einer IIS-Sub-Application tut. Muss
    /// vor dem ersten Client-Zugriff (lazily Host-Boot) aufgerufen werden.
    /// </summary>
    protected void UsePathBase(string pathBase) =>
        _factory.Server.BaseAddress = new Uri("http://localhost" + pathBase + "/");

    /// <summary>HTTP-Client auf den TestServer-Host (löst den Host-Boot lazily aus).</summary>
    protected HttpClient Client => _factory.CreateClient();

    /// <summary>
    /// HTTP-Client mit eigener BaseAddress: der TestServer setzt den Basis-Pfad als
    /// <c>Request.PathBase</c> — genau das, was ANCM bei einer IIS-Sub-Application
    /// tut. Basis für die PathBase-Deployment-Tests (<see cref="HostPathBaseTests"/>).
    /// </summary>
    protected HttpClient CreateClient(Uri baseAddress, bool allowAutoRedirect = true) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = baseAddress,
            AllowAutoRedirect = allowAutoRedirect,
        });

    /// <summary>HTTP-Client OHNE Auto-Redirect (für 302-Assertions).</summary>
    protected HttpClient ClientNoRedirect =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Zugriff auf den DI-Container (z. B. um <see cref="IHeimdallQuery"/> aufzulösen).</summary>
    protected IServiceProvider Services => _factory.Services;

    /// <summary>Der registrierte <see cref="IHeimdallQuery"/>-Singleton (der SQLite-Sink).</summary>
    protected IHeimdallQuery Query => _factory.Services.GetRequiredService<IHeimdallQuery>();

    /// <summary>
    /// Baut einen OTLP-Trace-Export-Request mit genau einem Span (Name <paramref name="spanName"/>,
    /// eigene Trace/Span-ID). Nutzt den kanonischen Proto-Typ aus Heimdall.Otlp.Proto.
    /// </summary>
    protected static ExportTraceServiceRequest BuildTraceRequest(string spanName)
    {
        var tid = new byte[] { 0xa1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var sid = new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0x01 };

        var req = new ExportTraceServiceRequest();
        var rs = new ResourceSpans
        {
            Resource = new Resource
            {
                Attributes =
                {
                    new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "host-test" } }
                }
            }
        };
        var ss = new ScopeSpans { Scope = new InstrumentationScope { Name = "test", Version = "1.0" } };
        ss.Spans.Add(new Span
        {
            TraceId = ByteString.CopyFrom(tid),
            SpanId = ByteString.CopyFrom(sid),
            Name = spanName,
            Kind = SpanKind.Server,
            StartTimeUnixNano = 1_000_000_000UL,
            EndTimeUnixNano = 1_800_000_000UL,
            Status = new Status { Code = Status.Types.StatusCode.Ok },
        });
        rs.ScopeSpans.Add(ss);
        req.ResourceSpans.Add(rs);
        return req;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _factory.Dispose(); } catch { }
        foreach (var k in _envKeys) Environment.SetEnvironmentVariable(k, null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
#endif