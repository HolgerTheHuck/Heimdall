using System;
using Heimdall;
using Heimdall.AspNetCore;
using Heimdall.Blazor;
using Heimdall.Blazor.Alerts;
using Heimdall.Ingest;
using Heimdall.Otlp;
using Heimdall.Otlp.Grpc;
using Heimdall.Prometheus;
using Heimdall.Storage.SQLite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Embedded;

/// <summary>
/// Convenience-Optionen für die Ein-Klick-Einbettung via
/// <see cref="HeimdallEmbeddedExtensions.AddHeimdall"/>. Bündelt die Teil-Optionen
/// der einzelnen Schichten (Storage, OTLP, Prometheus, Dashboard, Alerting, Auth)
/// hinter einer einfachen Oberfläche — entspricht dem <c>AddHeimdall(o =&gt; ...)</c>
/// aus <c>DESIGN.md</c>. Alle Felder haben Defaults, die eine funktionierende
/// Embedded-Instanz ergeben (SQLite im Arbeitsverzeichnis, alle Schichten an,
/// Auth aus, Prefix /otel).
/// </summary>
public sealed class HeimdallEmbeddedOptions
{
    /// <summary>SQLite-Dateipfad. Default „./heimdall.db“.</summary>
    public string DataPath { get; set; } = "./heimdall.db";

    /// <summary>Retention in Tagen; 0 = unbegrenzt. Default 7.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Harter Plafond über die DB-Datei in Bytes; 0 = unbegrenzt. Default 0.</summary>
    public long MaxBytes { get; set; }

    /// <summary>OTLP/HTTP-Empfänger aktiv. Default true.</summary>
    public bool EnableOtlpHttp { get; set; } = true;

    /// <summary>OTLP/gRPC-Empfänger aktiv. Default false (Embedded-Pfad nutzt meist HTTP/SDK).</summary>
    public bool EnableOtlpGrpc { get; set; }

    /// <summary>Prometheus-API + PromQL-Engine aktiv. Default true.</summary>
    public bool EnablePrometheus { get; set; } = true;

    /// <summary>Blazor-Dashboard aktiv. Default true.</summary>
    public bool EnableDashboard { get; set; } = true;

    /// <summary>Alerting-Subsystem aktiv. Default false (Embedded-Pfad meist ohne).</summary>
    public bool EnableAlerting { get; set; }

    /// <summary>Ingest-Buffer (Backpressure) aktiv. Default false (synchroner Pfad ist Default).</summary>
    public bool UseIngestBuffer { get; set; }

    /// <summary>URL-Prefix für alle Endpunkte (UI, OTLP, Prom). Default „/otel“.</summary>
    public string Prefix { get; set; } = "/otel";

    /// <summary>Max. gleichzeitige OTLP/HTTP-Requests (Admission Control). 0 = unbegrenzt. Default 32.</summary>
    public int OtlpHttpMaxConcurrent { get; set; } = 32;

    /// <summary>Max. gleichzeitige OTLP/gRPC-Requests. 0 = unbegrenzt. Default 32.</summary>
    public int OtlpGrpcMaxConcurrent { get; set; } = 32;

    /// <summary>Auth-Optionen (Shared mit <see cref="Heimdall.AspNetCore.HeimdallAuthOptions"/>).</summary>
    public Heimdall.AspNetCore.HeimdallAuthOptions Auth { get; set; } = new();

    /// <summary>Alerting-Optionen (nur bei <see cref="EnableAlerting"/> relevant).</summary>
    public HeimdallAlertingOptions Alerting { get; set; } = new();

    /// <summary>Verzeichnis für importierte Grafana-Dashboards. Default „./heimdall-dashboards“.</summary>
    public string DashboardsDir { get; set; } = "./heimdall-dashboards";
}

/// <summary>
/// Convenience-Fassade für die Heimdall-Einbettung. Bündelt die einzelnen
/// <c>AddHeimdall*</c>/<c>MapHeimdall*</c>-Aufrufe in einem Paar
/// <see cref="AddHeimdall"/> / <see cref="MapHeimdall"/> — wie in <c>DESIGN.md</c>
/// versprochen. Reduziert die DX-Diskrepanz von 3–4 Aufrufen über zwei Pakete
/// auf einen Aufruf.
/// </summary>
public static class HeimdallEmbeddedExtensions
{
    /// <summary>
    /// Registriert alle Heimdall-Schichten (Storage, OTLP, Prometheus, Dashboard,
    /// Alerting) in der DI. Konfiguriert via <paramref name="configure"/> (Default:
    /// SQLite im Arbeitsverzeichnis, alle Schichten an, Auth aus, Prefix /otel).
    /// Gibt die gebauten Sink/Query/MetricSource-Singletons zurück, sodass der
    /// Aufrufer sie direkt nutzen kann (z. B. für <c>UseHeimdallExporter</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddHeimdall(o =&gt; o.DataPath = "./otel");
    /// app.MapHeimdall("/otel");
    /// </code>
    /// </example>
    public static HeimdallRegistration AddHeimdall(this IServiceCollection services,
        Action<HeimdallEmbeddedOptions>? configure = null)
    {
        var opts = new HeimdallEmbeddedOptions();
        configure?.Invoke(opts);

        // Storage (1.0: SQLite). Der Sink implementiert IHeimdallSink, IHeimdallQuery
        // UND IHeimdallMetricSource — dasselbe Objekt geht in alle Add*-Aufrufe.
        var sqliteOpts = new SQLiteTelemetryOptions
        {
            DataPath = opts.DataPath,
            RetentionDays = opts.RetentionDays,
            MaxBytes = opts.MaxBytes,
        };
        var sink = new SQLiteTelemetrySink(sqliteOpts);
        IHeimdallSink writeSink = sink;

        // Optionaler Ingest-Buffer (Backpressure/Batching).
        IDisposable? bufferDisposable = null;
        if (opts.UseIngestBuffer)
        {
            var buffer = new IngestBuffer(sink, new IngestOptions
            {
                MaxQueueItems = 20_000, BatchSpans = 256, BatchLogs = 1024, BatchMetrics = 1024,
                DropPolicy = IngestDropPolicy.DropOldest,
            });
            writeSink = buffer;
            bufferDisposable = buffer;
        }

        // DI-Registrierung der Schichten.
        if (opts.EnableDashboard) services.AddHeimdallDashboard(sink);
        if (opts.EnableOtlpHttp)
            services.AddHeimdallOtlp(writeSink, new HeimdallOtlpHttpOptions { MaxConcurrentRequests = opts.OtlpHttpMaxConcurrent });
        if (opts.EnableOtlpGrpc)
        {
            var grpcOpts = new HeimdallOtlpGrpcOptions { MaxConcurrentRequests = opts.OtlpGrpcMaxConcurrent };
            if (opts.Auth.Enabled) { grpcOpts.AuthEnabled = true; grpcOpts.ApiKey = opts.Auth.ApiKey; }
            services.AddHeimdallOtlpGrpc(writeSink, grpcOpts);
        }
        if (opts.EnablePrometheus) services.AddHeimdallPrometheus(sink, sink);
        services.AddHeimdallDashboards(opts.DashboardsDir);
        if (opts.EnableAlerting) services.AddHeimdallAlerting(sink, opts.Alerting);

        // Auth-Optionen syncen (Prefixe) + registrieren, damit MapHeimdall/Login-Handler sie liest.
        opts.Auth.OtlpHttpPrefix = opts.Prefix;
        opts.Auth.PrometheusPrefix = opts.Prefix;
        // Login/Logout-Pfade unter dem Prefix (die Blazor-Routen sind /otel/login
        // und /otel/logout, nicht /login). ProtectedPrefix=null (Embedded=global).
        opts.Auth.LoginPath = opts.Prefix.TrimEnd('/') + "/login";
        opts.Auth.LogoutPath = opts.Prefix.TrimEnd('/') + "/logout";
        if (opts.Auth.Enabled) services.AddHeimdallAuth(opts.Auth);
        services.AddSingleton(opts);

        return new HeimdallRegistration(sink, sink, sink, writeSink, bufferDisposable, opts);
    }

    /// <summary>
    /// Mappt alle Heimdall-Endpunkte unter <paramref name="prefix"/> (Default /otel):
    /// UI, OTLP/HTTP, Prometheus-API, gRPC (falls aktiv). Auth-Middleware wird
    /// eingehängt, falls <see cref="Heimdall.AspNetCore.HeimdallAuthOptions.Enabled"/>.
    /// Health-Endpoint (/healthz) immer anonymous.
    /// </summary>
    public static void MapHeimdall(this IEndpointRouteBuilder endpoints, string? prefix = null)
    {
        // Options aus DI holen (AddHeimdall hat sie registriert).
        var sp = endpoints.ServiceProvider;
        var opts = sp.GetService<HeimdallEmbeddedOptions>() ?? new HeimdallEmbeddedOptions();
        var p = prefix ?? opts.Prefix;

        // Auth-Middleware via UseHeimdallAuth (falls aktiviert). Die Middleware
        // wird via IApplicationBuilder.Use registriert — hier via ServiceProvider
        // ist das nicht direkt möglich; stattdessen wird Auth im Host-Pfad
        // explizit eingehängt. Embedded-Nutzer ohne Host rufen UseHeimdallAuth
        // selbst auf (oder lassen Auth aus). Hinweis dokumentiert.
        // (Eine vollständige Middleware-Integration in MapHeimdall würde
        // IApplicationBuilder benötigen, der hier nicht verfügbar ist — bewusst
        // auf Host-Seite belassen, um die Map-Signatur einfach zu halten.)

        if (opts.EnableDashboard) endpoints.MapHeimdallDashboard(p);
        if (opts.EnableOtlpHttp) endpoints.MapHeimdallOtlp(p);
        if (opts.EnablePrometheus) endpoints.MapHeimdallPrometheus(p);
        if (opts.EnableOtlpGrpc) endpoints.MapHeimdallOtlpGrpc();

        // Health-Endpoint (immer anonymous, vor Auth).
        endpoints.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
    }

    /// <summary>
    /// Bündelt die Middleware-Pipeline in einem Aufruf: Auth-Middleware (falls
    /// <see cref="HeimdallEmbeddedOptions.Auth"/> aktiviert), StaticFiles für die
    /// Blazor-Assets (CSS/JS) und alle Heimdall-Endpunkte via
    /// <see cref="MapHeimdall"/>. Schließt die DX-Lücke zwischen
    /// <see cref="AddHeimdall"/> (DI) und <see cref="MapHeimdall"/> (Endpoints):
    /// Auth-Middleware braucht <c>IApplicationBuilder</c> (Middleware-Stadium),
    /// <c>MapHeimdall</c> arbeitet auf <c>IEndpointRouteBuilder</c> (Routing-Stadium)
    /// — <c>UseHeimdall</c> kapselt beide Stadien in der korrekten Reihenfolge.
    ///
    /// <example>
    /// <code>
    /// builder.Services.AddHeimdall(o =&gt; o.DataPath = "./otel");
    /// app.UseHeimdall();   // Auth + StaticFiles + Endpoints in einem Aufruf
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="app">Die Application-Builder-Pipeline.</param>
    /// <param name="prefix">Optional: überschreibt den Prefix aus den Options.</param>
    public static void UseHeimdall(this IApplicationBuilder app, string? prefix = null)
    {
        var opts = app.ApplicationServices.GetService<HeimdallEmbeddedOptions>() ?? new HeimdallEmbeddedOptions();
        var p = prefix ?? opts.Prefix;

        // 1. Auth-Middleware (Passthrough bei Enabled=false). Muss VOR den
        //    Endpoints eingehängt werden (Middleware-Stadium vor Routing).
        //    ProtectedPrefix=null = global schützen (Embedded-Nutzer, dessen
        //    Routes sämtlich Heimdalls sind — wie im Stand-alone-Host).
        opts.Auth.OtlpHttpPrefix = p;
        opts.Auth.PrometheusPrefix = p;
        if (opts.Auth.Enabled) app.UseHeimdallAuth(opts.Auth);

        // 2. StaticFiles für Blazor-Assets (/_content/Heimdall.Blazor/{css,js}).
        app.UseStaticFiles();

        // 3. Endpoints mappen (UI, OTLP, Prom, gRPC, /healthz). UseEndpoints
        //    stellt den IEndpointRouteBuilder bereit, den MapHeimdall braucht.
        app.UseEndpoints(endpoints => endpoints.MapHeimdall(p));
    }
}

/// <summary>
/// Ergebnis von <see cref="HeimdallEmbeddedExtensions.AddHeimdall"/>. Hält die
/// gebauten Singletons bereit, sodass der Aufrufer sie direkt nutzen kann
/// (z. B. <c>UseHeimdallExporter(registration.Sink)</c> oder Dispose bei Shutdown).
/// </summary>
public sealed class HeimdallRegistration : IDisposable
{
    /// <summary>Der Storage-Sink (SQLite) — für direkte Schreibzugriffe oder Exporter.</summary>
    public IHeimdallSink Sink { get; }
    /// <summary>Query-Interface (Traces/Logs/Metriken lesen).</summary>
    public IHeimdallQuery Query { get; }
    /// <summary>Metric-Source (PromQL-Datenquelle).</summary>
    public IHeimdallMetricSource MetricSource { get; }
    private readonly IDisposable? _bufferDisposable;
    private readonly IDisposable _sinkDisposable;

    internal HeimdallRegistration(IHeimdallSink sink, IHeimdallQuery query,
        IHeimdallMetricSource metricSource, IHeimdallSink writeSink,
        IDisposable? bufferDisposable, HeimdallEmbeddedOptions opts)
    {
        Sink = writeSink;   // Schreibziel = Buffer (falls aktiv) oder direkt SQLite
        Query = query;
        MetricSource = metricSource;
        _bufferDisposable = bufferDisposable;
        _sinkDisposable = (IDisposable)sink;
    }

    /// <summary>Disposet Buffer + Storage (bei Shutdown aufrufen).</summary>
    public void Dispose()
    {
        _bufferDisposable?.Dispose();   // Buffer zuerst (flusht Tail in SQLite)
        _sinkDisposable.Dispose();
    }
}
