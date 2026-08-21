using System.Net.Http;
using Heimdall;
using Heimdall.Blazor.Alerts;
using Heimdall.Blazor.Grafana;
using Heimdall.Prometheus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Heimdall.Blazor;

/// <summary>
/// DI-Registrierung fuer das Heimdall-Dashboard. Registriert die uebergebene
/// <see cref="IHeimdallQuery"/>-Instanz als Singleton (sodass die Komponenten per
/// <c>@inject IHeimdallQuery</c> darauf zugreifen koennen) und aktiviert die fuer
/// statisches SSR noetige Komponenten-Infrastruktur (<c>AddRazorComponents</c>).
/// </summary>
public static class HeimdallServiceExtensions
{
    public static IServiceCollection AddHeimdallDashboard(this IServiceCollection services, IHeimdallQuery query)
    {
        if (query is null) throw new System.ArgumentNullException(nameof(query));
        services.AddRazorComponents();          // EndpointHtmlRenderer fuer RazorComponentResult
        services.AddSingleton(query);
        return services;
    }

    /// <summary>
    /// Aktiviert den eingebauten Grafana-Dashboard-Renderer: registriert einen
    /// dateibasierten <see cref="IGrafanaDashboardStore"/> im Verzeichnis
    /// <paramref name="dashboardsDir"/> (ueberlebt Neustart). Voraussetzung: die
    /// PromQL-Engine ist bereits registriert — das geschieht ueber
    /// <c>AddHeimdallPrometheus</c>, das <c>PromEngine</c> als Singleton in den
    /// Container legt. Aufruf im Host:
    /// <code>
    /// builder.Services.AddHeimdallDashboard(sink)
    ///                 .AddHeimdallPrometheus(sink, sink)
    ///                 .AddHeimdallDashboards(dashboardsDir);
    /// </code>
    /// </summary>
    public static IServiceCollection AddHeimdallDashboards(this IServiceCollection services, string dashboardsDir)
    {
        if (string.IsNullOrWhiteSpace(dashboardsDir))
            throw new System.ArgumentException("Dashboard-Verzeichnis fehlt", nameof(dashboardsDir));
        services.AddSingleton<IGrafanaDashboardStore>(new FileGrafanaDashboardStore(dashboardsDir));
        return services;
    }

    /// <summary>
    /// Aktiviert das Alarm-Subsystem (Regel-Store + Zustands-Store immer; Evaluator
    /// + Kanäle nur wenn <c>opts.Enabled</c>). Der Regel-Store liegt unter
    /// <c>opts.RulesDir</c>, der Zustands-Store unter <c>opts.StateDir</c>
    /// (alertstate.json) — beide ueberleben Neustart. Die <see cref="IHeimdallQuery"/>
    /// ist bereits via <c>AddHeimdallDashboard</c> registriert; <see cref="PromEngine"/>
    /// wird optional (falls via <c>AddHeimdallPrometheus</c> registriert) fuer
    /// Metrik-Regeln herangezogen. Neue <c>Add*</c>-Methode — bestaehende
    /// Signaturen unangetastet.
    /// </summary>
    public static IServiceCollection AddHeimdallAlerting(this IServiceCollection services, IHeimdallQuery query, HeimdallAlertingOptions opts)
    {
        if (opts is null) throw new System.ArgumentNullException(nameof(opts));

        // Store + Options IMMER (UI /otel/alerts funktioniert auch ohne aktiven Evaluator).
        services.AddSingleton<IAlertRuleStore>(new FileAlertRuleStore(opts.RulesDir));
        services.AddSingleton<IAlertStateStore>(new FileAlertStateStore(opts.StateDir));
        services.AddSingleton(opts);

        if (!opts.Enabled) return services;

        // Kanäle (bedingt): Logger immer verfuegbar, SMTP/Webhook nur wenn konfiguriert.
        if (opts.LoggerEnabled)
            services.AddSingleton<IAlertChannel, LoggerAlertChannel>();
        if (opts.Smtp is { Enabled: true })
            services.AddSingleton<IAlertChannel>(sp => new SmtpAlertChannel(
                opts.Smtp, sp.GetRequiredService<ILogger<SmtpAlertChannel>>()));
        if (opts.Webhook is { Enabled: true })
        {
            services.AddHttpClient();
            services.AddSingleton<IAlertChannel>(sp => new WebhookAlertChannel(
                opts.Webhook, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<WebhookAlertChannel>>()));
        }

        // Evaluator als Singleton + HostedService (sauberes Start/Stop on boot/shutdown).
        services.AddSingleton<AlertEvaluator>(sp => new AlertEvaluator(
            query ?? sp.GetRequiredService<IHeimdallQuery>(),
            sp.GetRequiredService<IAlertRuleStore>(),
            sp.GetRequiredService<IAlertStateStore>(),
            sp.GetServices<IAlertChannel>(),
            sp.GetService<PromEngine>(),                       // null → Metrik-Regeln uebersprungen
            sp.GetRequiredService<ILogger<AlertEvaluator>>(),
            opts));
        services.AddHostedService<AlertEvaluator>(sp => sp.GetRequiredService<AlertEvaluator>());
        return services;
    }
}