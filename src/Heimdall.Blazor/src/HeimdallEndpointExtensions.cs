using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Globalization;
using System.Linq;
using Heimdall.Blazor.Alerts;
using Heimdall.Blazor.Grafana;

namespace Heimdall.Blazor;

/// <summary>
/// Mappt das Heimdall-Dashboard unter ein Praefix (Default <c>/otel</c>). Alle Seiten
/// sind server-gerendert (statisches SSR ueber <see cref="RazorComponentResult{TComponent}"/>),
/// Filter laufen ueber GET-Query-Parameter — kein SignalR, kein JavaScript. Jede Route
/// nimmt optional den konfigurierbaren Zeitraum (<c>preset</c>/<c>from</c>/<c>to</c>),
/// den <see cref="HeimdallRange"/> in Unix-Nanosekunden-Schranken aufloest.
///
/// Aufruf im Host:
/// <code>
/// app.MapHeimdallDashboard("/otel");
/// </code>
/// </summary>
public static class HeimdallEndpointExtensions
{
    public static IEndpointConventionBuilder MapHeimdallDashboard(this IEndpointRouteBuilder endpoints, string prefix = "/otel")
    {
        var group = endpoints.MapGroup(prefix);

        // Login-Seite + Login/Logout-Handler. Auth-Options aus DI (wenn
        // UseHeimdallAuth/AddHeimdallAuth registriert hat); fehlen sie, ist
        // Auth deaktiviert und die Login-Seite ist nicht erreichbar (die
        // Middleware redirectet nicht). ReturnUrl wird nach Login wieder
        // aufgenommen. CSRF via CheckSameOrigin (wie die anderen POST-Endpoints).
        group.MapGet("/login", (HttpContext ctx, string? returnUrl) =>
        {
            var auth = ctx.RequestServices.GetService<Heimdall.AspNetCore.HeimdallAuthOptions>();
            var err = ctx.Request.Query["err"].ToString();
            var lastUser = ctx.Request.Query["user"].ToString();
            return new RazorComponentResult<LoginPage>(new
            {
                BasePath = prefix,
                ReturnUrl = string.IsNullOrEmpty(returnUrl) ? prefix : returnUrl,
                Error = string.IsNullOrEmpty(err) ? null : err,
                LastUser = string.IsNullOrEmpty(lastUser) ? null : lastUser,
            });
        });

        group.MapPost("/login", async (HttpContext ctx) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var auth = ctx.RequestServices.GetService<Heimdall.AspNetCore.HeimdallAuthOptions>();
            if (auth is null || !auth.Enabled)
                return Results.Redirect($"{prefix}/login?err=Auth+nicht+aktiv");

            var form = await ctx.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();
            if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith(prefix, StringComparison.Ordinal))
                returnUrl = prefix;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) ||
                !Heimdall.AspNetCore.HeimdallSessionCookie.CheckCredentials(username, password, auth))
            {
                var err = Uri.EscapeDataString("Benutzername oder Passwort falsch");
                var user = Uri.EscapeDataString(username ?? string.Empty);
                return Results.Redirect($"{prefix}/login?err={err}&user={user}");
            }

            Heimdall.AspNetCore.HeimdallSessionCookie.Issue(ctx.Response, auth, username);
            return Results.Redirect(returnUrl);
        });

        group.MapPost("/logout", (HttpContext ctx) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var auth = ctx.RequestServices.GetService<Heimdall.AspNetCore.HeimdallAuthOptions>();
            if (auth is not null) Heimdall.AspNetCore.HeimdallSessionCookie.Clear(ctx.Response, auth);
            return Results.Redirect($"{prefix}/login");
        });

        // Landing / Übersicht (Health-KPIs, neueste Fehler-Traces/Logs, Quick-Nav).
        group.MapGet("/", () =>
            new RazorComponentResult<HomePage>(new { BasePath = prefix }));

        group.MapGet("/traces", (string? name, string? svc, string? err, string? limit, string? offset, string? preset, string? from, string? to) =>
            new RazorComponentResult<TracesPage>(new
            {
                BasePath = prefix,
                NameContains = name,
                ServiceName = svc,
                HasError = ParseErr(err),
                Limit = ParseInt(limit) ?? 100,
                Offset = ParseInt(offset) ?? 0,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/trace/{tid}", (string tid) =>
            new RazorComponentResult<TraceDetailPage>(new { BasePath = prefix, TraceId = tid }));

        group.MapGet("/logs", (string? text, string? q, string? sev, string? limit, string? expand, string? preset, string? from, string? to) =>
            new RazorComponentResult<LogsPage>(new
            {
                BasePath = prefix,
                Text = text,
                Query1 = q,
                MinSeverity = ParseInt(sev),
                Limit = ParseInt(limit) ?? 200,
                Expand = expand == "1",
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/metrics", (string? name, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<MetricsPage>(new
            {
                BasePath = prefix,
                Name = name,
                Limit = ParseInt(limit) ?? 500,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        // Verbundenes Monitoring: Load, Antwortzeiten (p50/p95/p99 aus dem
        // http.server.request.duration-Histogramm), Errorrate, Uptime, Spitzenlast,
        // plus „Logs im Zeitraum" und „Traces im Zeitraum" — alles über den
        // konfigurierbaren Zeitraum. Default-Preset 24h.
        group.MapGet("/dashboard", (string? requests, string? errors, string? duration, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<DashboardPage>(new
            {
                BasePath = prefix,
                Requests = requests,
                Errors = errors,
                // Duration ist die OTel-Semantik (http.server.request.duration) — kein
                // Demo-Artefakt wie früher „orders"/„orders.errors", daher Default beibehalten.
                Duration = string.IsNullOrWhiteSpace(duration) ? "http.server.request.duration" : duration,
                Limit = ParseInt(limit) ?? 500,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        // Controller/Endpoint-Drilldown (ASP.NET-WebAPI). Aggregiert Server-Spans
        // über HeimdallEndpointAgg → Gesamt-API / Controller / Endpoint (Action).
        // /endpoints = Overview (Controller-Tabelle), /endpoints/{controller} =
        // Endpoints dieses Controllers. Dimensions-Attribute (Defaults vom
        // Heimdall.AspNetCore-Plugin) sind per Query-Param überschreibbar.
        group.MapGet("/endpoints", (string? controllerAttr, string? actionAttr, string? routeAttr, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<EndpointsPage>(new
            {
                BasePath = prefix,
                Controller = (string?)null,
                ControllerAttr = string.IsNullOrWhiteSpace(controllerAttr) ? "aspnetmvc.controller" : controllerAttr,
                ActionAttr = string.IsNullOrWhiteSpace(actionAttr) ? "aspnetmvc.action" : actionAttr,
                RouteAttr = string.IsNullOrWhiteSpace(routeAttr) ? "http.route" : routeAttr,
                Limit = ParseInt(limit) ?? 5000,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/endpoints/{controller}", (string controller, string? controllerAttr, string? actionAttr, string? routeAttr, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<EndpointsPage>(new
            {
                BasePath = prefix,
                Controller = controller,
                ControllerAttr = string.IsNullOrWhiteSpace(controllerAttr) ? "aspnetmvc.controller" : controllerAttr,
                ActionAttr = string.IsNullOrWhiteSpace(actionAttr) ? "aspnetmvc.action" : actionAttr,
                RouteAttr = string.IsNullOrWhiteSpace(routeAttr) ? "http.route" : routeAttr,
                Limit = ParseInt(limit) ?? 5000,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        // === Importierte Grafana-Dashboards (selbststaendig in Heimdall gerendert) ===
        // Liste + Import + Detailansicht. Template-Variablen kommen als var-* Query-
        // Parameter (GET-Form, kein JS) und werden im Handler in ein Dict gehoben.
        group.MapGet("/dashboards", () =>
            new RazorComponentResult<GrafanaDashboardsPage>(new { BasePath = prefix }));

        group.MapGet("/dashboards/import", (string? err) =>
            new RazorComponentResult<GrafanaImportPage>(new { BasePath = prefix, Error = err }));

        group.MapPost("/dashboards/import", async (HttpContext ctx, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var form = await ctx.Request.ReadFormAsync();
            string? content = null;
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is not null && file.Length > 0)
            {
                using var sr = new System.IO.StreamReader(file.OpenReadStream());
                content = await sr.ReadToEndAsync();
            }
            else if (!string.IsNullOrWhiteSpace(form["json"])) content = form["json"];
            if (string.IsNullOrWhiteSpace(content))
                return Results.Redirect($"{prefix}/dashboards/import?err=Kein+Dashboard-JSON");
            try { var uid = store.Save(content!); return Results.Redirect($"{prefix}/dashboards/{uid}"); }
            catch (Exception ex) { return Results.Redirect($"{prefix}/dashboards/import?err=" + Uri.EscapeDataString(ex.Message)); }
        });

        group.MapGet("/dashboards/{uid}", (HttpContext ctx, string uid, string? preset, string? from, string? to) =>
        {
            var vars = ctx.Request.Query
                .Where(k => k.Key.StartsWith("var-", StringComparison.Ordinal))
                .ToDictionary(k => k.Key.Substring(4), k => k.Value.ToString(), StringComparer.Ordinal);
            return new RazorComponentResult<GrafanaDashboardViewPage>(new
            {
                BasePath = prefix, Uid = uid, Preset = preset, From = ParseNs(from), To = ParseNs(to), Vars = vars,
            });
        });

        group.MapPost("/dashboards/{uid}/delete", (HttpContext ctx, string uid, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            store.Delete(uid);
            return Results.Redirect($"{prefix}/dashboards");
        });

        // === Alarm-Subsystem (Regeln ueber Logs/Metriken/Traces) ===
        // Liste + Editor + Detail. Store/UI immer verfuegbar (auch ohne aktiven Evaluator);
        // Auth-Abdeckung erbt der Host via Prefix-Middleware.
        group.MapGet("/alerts", (string? state, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<AlertsPage>(new
            {
                BasePath = prefix,
                StateFilter = state,
                Limit = ParseInt(limit) ?? 100,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/alerts/new", (string? err) =>
            new RazorComponentResult<AlertRuleEditPage>(new { BasePath = prefix, Id = (string?)null, Error = err }));

        group.MapGet("/alerts/{id}/edit", (string id, string? err) =>
            new RazorComponentResult<AlertRuleEditPage>(new { BasePath = prefix, Id = id, Error = err }));

        group.MapGet("/alerts/{id}", (string id) =>
            new RazorComponentResult<AlertDetailPage>(new { BasePath = prefix, Id = id }));

        group.MapPost("/alerts/save", async (HttpContext ctx, IAlertRuleStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var form = await ctx.Request.ReadFormAsync();
            var id = form["id"].ToString();
            var name = form["name"].ToString();
            if (string.IsNullOrWhiteSpace(name))
                return Results.Redirect($"{prefix}/alerts/new?err=Regelname+fehlt");
            if (!Enum.TryParse<AlertSignal>(form["signal"].ToString(), true, out var signal))
                signal = AlertSignal.Metric;
            var channels = form["channels"].Select(v => v?.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList<string>();
            var rule = new AlertRule(
                Id: id,
                Name: name,
                Enabled: form["enabled"].Count > 0,
                Signal: signal,
                Promql: NullIfEmpty(form["promql"].ToString()),
                LogText: NullIfEmpty(form["logText"].ToString()),
                MinSeverity: ParseInt(form["minSeverity"]),
                HasError: ParseBool(form["hasError"]),
                ServiceName: NullIfEmpty(form["serviceName"].ToString()),
                NameContains: NullIfEmpty(form["nameContains"].ToString()),
                WindowSeconds: ParseLong(form["windowSeconds"]) ?? 300,
                Threshold: ParseInt(form["threshold"]) ?? 0,
                ForSeconds: ParseLong(form["forSeconds"]) ?? 0,
                Channels: channels,
                Description: NullIfEmpty(form["description"].ToString()),
                EvalIntervalSeconds: ParseLong(form["evalInterval"]) ?? 0);
            try
            {
                var savedId = store.Save(rule);
                return Results.Redirect($"{prefix}/alerts/{savedId}");
            }
            catch (Exception ex)
            {
                var back = string.IsNullOrEmpty(id) ? $"{prefix}/alerts/new" : $"{prefix}/alerts/{id}/edit";
                return Results.Redirect(back + "?err=" + Uri.EscapeDataString(ex.Message));
            }
        });

        group.MapPost("/alerts/{id}/delete", (HttpContext ctx, string id, IAlertRuleStore store, IAlertStateStore stateStore) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            store.Delete(id);
            stateStore.Remove(id);
            return Results.Redirect($"{prefix}/alerts");
        });

        return group;
    }

    private static bool? ParseErr(string? err) => err switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    /// <summary>
    /// Bindet <c>from</c>/<c>to</c> als Zeichenkette und wandelt sie tolerant in Unix-ns
    /// um. Leere/whitespace/unparsebare Werte (z. B. die leeren hidden-Inputs aus
    /// <see cref="HeimdallTimeRange"/> bei Preset-Submit) werden zu <c>null</c> — im
    /// Gegensatz zu <c>long?</c>-Direktbindung, die bei leerem Query-Wert eine
    /// <c>BadHttpRequestException</c> wirft.
    /// </summary>
    private static long? ParseNs(string? s) =>
        long.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int? ParseInt(Microsoft.Extensions.Primitives.StringValues v) =>
        int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long? ParseLong(Microsoft.Extensions.Primitives.StringValues v) =>
        long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool? ParseBool(Microsoft.Extensions.Primitives.StringValues v)
    {
        var s = v.ToString();
        if (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>
    /// CSRF-Schutz für die zustandsändernden POST-Endpoints (Dashboard-Import/Delete,
    /// Alert-Save/Delete). Bei Basic-Auth werden Credentials cross-site automatisch
    /// mitgesendet — der Angreifer kann die Response zwar nicht lesen (SOP), aber
    /// zustandsändernde POSTs wären ohne diesen Check möglich. Origin/Referer-Check
    /// ist der OWASP-empfohlene, JavaScript-freie Schutz für nicht-Cookie-Auth-UIs:
    /// ein Cross-Site-Form-POST setzt einen anderen Origin-Header (oder keinen),
    /// den der Browser nicht fälschen kann. Same-Site-Requests (leerer Origin bei
    /// GET-Form-Navigation, gleicher Host bei POST) werden akzeptiert.
    /// </summary>
    private static bool CheckSameOrigin(HttpContext ctx, string prefix)
    {
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Manche Browser unterdrücken Origin bei same-origin POST; dann Referer.
            var referer = ctx.Request.Headers["Referer"].ToString();
            if (string.IsNullOrEmpty(referer)) return true;   // kein Header → SameSite
            if (!Uri.TryCreate(referer, UriKind.Absolute, out var refUri)) return true;
            return StringComparer.OrdinalIgnoreCase.Equals(refUri.Host, ctx.Request.Host.Host) &&
                   refUri.Port == ctx.Request.Host.Port;
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return StringComparer.OrdinalIgnoreCase.Equals(uri.Host, ctx.Request.Host.Host) &&
               uri.Port == ctx.Request.Host.Port;
    }
}