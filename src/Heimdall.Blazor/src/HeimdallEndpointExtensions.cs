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
        // full = externer Prefix (Request.PathBase + prefix) — unter IIS-
        // Unterverzeichnis/Proxy-Pfad-Strip schleppt er das Deployment-Verzeichnis
        // mit (siehe HeimdallUiPaths); am Site-Root identisch mit prefix.
        group.MapGet("/login", (HttpContext ctx, string? returnUrl) =>
        {
            var auth = ctx.RequestServices.GetService<Heimdall.AspNetCore.HeimdallAuthOptions>();
            var err = ctx.Request.Query["err"].ToString();
            var lastUser = ctx.Request.Query["user"].ToString();
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            return new RazorComponentResult<LoginPage>(new
            {
                BasePath = full,
                ReturnUrl = string.IsNullOrEmpty(returnUrl) ? full : returnUrl,
                Error = string.IsNullOrEmpty(err) ? null : err,
                LastUser = string.IsNullOrEmpty(lastUser) ? null : lastUser,
            });
        });

        group.MapPost("/login", async (HttpContext ctx) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var auth = ctx.RequestServices.GetService<Heimdall.AspNetCore.HeimdallAuthOptions>();
            if (auth is null || !auth.Enabled)
            {
                var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
                return Results.Redirect($"{full}/login?err=" + Uri.EscapeDataString(i18n.T("login.error.noauth")));
            }

            var form = await ctx.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();
            // Open-Redirect-Schutz gegen den externen Prefix (returnUrl kommt aus
            // dem Auth-Redirect bzw. dem Login-Formular und trägt die PathBase).
            if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith(full, StringComparison.Ordinal))
                returnUrl = full;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) ||
                !Heimdall.AspNetCore.HeimdallSessionCookie.CheckCredentials(username, password, auth))
            {
                var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
                var err = Uri.EscapeDataString(i18n.T("login.error.badcreds"));
                var user = Uri.EscapeDataString(username ?? string.Empty);
                return Results.Redirect($"{full}/login?err={err}&user={user}");
            }

            Heimdall.AspNetCore.HeimdallSessionCookie.Issue(ctx.Response, auth, username);
            return Results.Redirect(returnUrl);
        });

        group.MapPost("/logout", (HttpContext ctx) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var auth = ctx.RequestServices.GetService<Heimdall.AspNetCore.HeimdallAuthOptions>();
            if (auth is not null) Heimdall.AspNetCore.HeimdallSessionCookie.Clear(ctx.Response, auth);
            return Results.Redirect(HeimdallUiPaths.FullPrefix(ctx, prefix) + "/login");
        });

        // Landing / Übersicht (Health-KPIs, neueste Fehler-Traces/Logs, Quick-Nav).
        group.MapGet("/", (HttpContext ctx) =>
            new RazorComponentResult<HomePage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix) }));

        // Drilldown-Sprungseite zu Traces/Logs/Metriken (wie Grafana „Drilldown").
        group.MapGet("/drilldown", (HttpContext ctx) =>
            new RazorComponentResult<DrilldownPage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix) }));

        group.MapGet("/traces", (HttpContext ctx, string? name, string? svc, string? ver, string? err, string? limit, string? offset, string? sort, string? dir, string? preset, string? from, string? to) =>
            new RazorComponentResult<TracesPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
                NameContains = name,
                // Selects schicken bei „alle" leere Values mit -> null.
                ServiceName = NullIfEmpty(svc ?? ""),
                ServiceVersion = NullIfEmpty(ver ?? ""),
                HasError = ParseErr(err),
                Limit = ParseInt(limit) ?? 100,
                Offset = ParseInt(offset) ?? 0,
                Sort = sort,
                Dir = dir,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/trace/{tid}", (HttpContext ctx, string tid) =>
            new RazorComponentResult<TraceDetailPage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), TraceId = tid }));

        group.MapGet("/logs", (HttpContext ctx, string? text, string? q, string? sev, string? svc, string? ver, string? limit, string? expand, string? offset, string? sort, string? dir, string? preset, string? from, string? to) =>
            new RazorComponentResult<LogsPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
                Text = text,
                Query1 = q,
                MinSeverity = ParseInt(sev),
                // Selects schicken bei „alle" leere Values mit -> null, sonst
                // wuerde ein leerer String als Filterwert interpretiert.
                ServiceName = NullIfEmpty(svc ?? ""),
                ServiceVersion = NullIfEmpty(ver ?? ""),
                Limit = ParseInt(limit) ?? 200,
                Expand = expand == "1",
                Offset = ParseInt(offset) ?? 0,
                Sort = sort,
                Dir = dir,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/metrics", (HttpContext ctx, string? name, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<MetricsPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
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
        group.MapGet("/dashboard", (HttpContext ctx, string? requests, string? errors, string? duration, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<DashboardPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
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
        group.MapGet("/endpoints", (HttpContext ctx, string? controllerAttr, string? actionAttr, string? routeAttr, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<EndpointsPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
                Controller = (string?)null,
                ControllerAttr = string.IsNullOrWhiteSpace(controllerAttr) ? "aspnetmvc.controller" : controllerAttr,
                ActionAttr = string.IsNullOrWhiteSpace(actionAttr) ? "aspnetmvc.action" : actionAttr,
                RouteAttr = string.IsNullOrWhiteSpace(routeAttr) ? "http.route" : routeAttr,
                Limit = ParseInt(limit) ?? 5000,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/endpoints/{controller}", (HttpContext ctx, string controller, string? controllerAttr, string? actionAttr, string? routeAttr, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<EndpointsPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
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
        group.MapGet("/dashboards", (HttpContext ctx) =>
            new RazorComponentResult<GrafanaDashboardsPage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix) }));

        group.MapGet("/dashboards/import", (HttpContext ctx, string? err) =>
            new RazorComponentResult<GrafanaImportPage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Error = err }));

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
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);   // PathBase mitschleppen
            if (string.IsNullOrWhiteSpace(content))
            {
                var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
                return Results.Redirect($"{full}/dashboards/import?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.nodashboardjson")));
            }
            try { var uid = store.Save(content!); return Results.Redirect($"{full}/dashboards/{uid}"); }
            catch (Exception ex) { return Results.Redirect($"{full}/dashboards/import?err=" + Uri.EscapeDataString(ex.Message)); }
        });

        group.MapGet("/dashboards/{uid}", (HttpContext ctx, string uid, string? preset, string? from, string? to) =>
        {
            var vars = ctx.Request.Query
                .Where(k => k.Key.StartsWith("var-", StringComparison.Ordinal))
                .ToDictionary(k => k.Key.Substring(4), k => k.Value.ToString(), StringComparer.Ordinal);
            // Zurück-Link zum zuvor angesehenen Dashboard (Overview → Detail →
            // Zurück → Overview). Nur interne Dashboard-Routen werden akzeptiert
            // (Open-Redirect-Schutz); sonst null (Browser-Back bleibt Reserve).
            string? backUrl = ResolveBackUrl(ctx, prefix, uid);

            return new RazorComponentResult<GrafanaDashboardViewPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
                Uid = uid, Preset = preset, From = ParseNs(from), To = ParseNs(to), Vars = vars,
                BackUrl = backUrl,
            });
        });

        // Per-Panel-Fragment für das Lazy-Loading: die Shell rendert sofort
        // Platzhalter, dieser Endpoint liefert pro Panel (Index in ExpandPanels)
        // das ausgewertete Fragment. Storage erlaubt konkurrente Reads (WAL),
        // d. h. parallele Panel-Fetches laufen wirklich concurrently. idx ist die
        // Position in der Render-Slot-Liste (nicht GrafanaPanel.Id — das ist bei
        // Repeat-Expansion nicht eindeutig). Ohne JS öffnet der No-JS-Link der
        // Shell dieses Fragment direkt (stylt via inline <link>).
        group.MapGet("/dashboards/{uid}/panel/{idx:int}", (HttpContext ctx, string uid, int idx, string? preset, string? from, string? to) =>
        {
            var store = ctx.RequestServices.GetRequiredService<IGrafanaDashboardStore>();
            var dash = store.Get(uid);
            if (dash is null) return Results.NotFound();

            var engine = ctx.RequestServices.GetRequiredService<Heimdall.Prometheus.PromEngine>();
            var query = ctx.RequestServices.GetRequiredService<Heimdall.IHeimdallQuery>();
            var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();

            var vars = ctx.Request.Query
                .Where(k => k.Key.StartsWith("var-", StringComparison.Ordinal))
                .ToDictionary(k => k.Key.Substring(4), k => k.Value.ToString(), StringComparer.Ordinal);

            var prep = GrafanaDashboardRender.BuildRenderVars(dash, vars, preset, ParseNs(from), ParseNs(to), HeimdallRange.NowUnixNano());
            var slots = GrafanaDashboardRender.ExpandPanels(dash, prep.RenderVars);
            if (idx < 0 || idx >= slots.Count) return Results.NotFound();

            var slot = slots[idx];
            var titled = slot.Panel with { Title = slot.Title };
            var rp = GrafanaPanelRenderer.Render(titled, engine, prep.FromMs, prep.ToMs, prep.StepMs, slot.Vars, query, i18n.Lang, HeimdallUiPaths.FullPrefix(ctx, prefix));
            return new RazorComponentResult<GrafanaPanelFragment>(new { Panel = rp, BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix) });
        });

        group.MapPost("/dashboards/{uid}/delete", (HttpContext ctx, string uid, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            store.Delete(uid);
            return Results.Redirect(HeimdallUiPaths.FullPrefix(ctx, prefix) + "/dashboards");
        });

        // === Alarm-Subsystem (Regeln ueber Logs/Metriken/Traces) ===
        // Liste + Editor + Detail. Store/UI immer verfuegbar (auch ohne aktiven Evaluator);
        // Auth-Abdeckung erbt der Host via Prefix-Middleware.
        group.MapGet("/alerts", (HttpContext ctx, string? state, string? limit, string? preset, string? from, string? to) =>
            new RazorComponentResult<AlertsPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
                StateFilter = state,
                Limit = ParseInt(limit) ?? 100,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
            }));

        group.MapGet("/alerts/new", (HttpContext ctx, string? err) =>
            new RazorComponentResult<AlertRuleEditPage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Id = (string?)null, Error = err }));

        group.MapGet("/alerts/{id}/edit", (HttpContext ctx, string id, string? err) =>
            new RazorComponentResult<AlertRuleEditPage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Id = id, Error = err }));

        group.MapGet("/alerts/{id}", (HttpContext ctx, string id) =>
            new RazorComponentResult<AlertDetailPage>(new { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Id = id }));

        group.MapPost("/alerts/save", async (HttpContext ctx, IAlertRuleStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var form = await ctx.Request.ReadFormAsync();
            var id = form["id"].ToString();
            var name = form["name"].ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
                return Results.Redirect($"{full}/alerts/new?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.rulename")));
            }
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
                return Results.Redirect($"{full}/alerts/{savedId}");
            }
            catch (Exception ex)
            {
                var back = string.IsNullOrEmpty(id) ? $"{full}/alerts/new" : $"{full}/alerts/{id}/edit";
                return Results.Redirect(back + "?err=" + Uri.EscapeDataString(ex.Message));
            }
        });

        group.MapPost("/alerts/{id}/delete", (HttpContext ctx, string id, IAlertRuleStore store, IAlertStateStore stateStore) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            store.Delete(id);
            stateStore.Remove(id);
            return Results.Redirect(HeimdallUiPaths.FullPrefix(ctx, prefix) + "/alerts");
        });

        // Sprache umschalten (de/en/fr): setzt den `heimdall-lang`-Cookie und
        // redirectet zurück. POST + CheckSameOrigin (wie alle State-Changes hier),
        // da State-Change via GET ein OWASP-Antipattern ist (cache-/CSRF-bar). Das
        // Flaggen-UI in HeimdallNav rendert kleine <form method=post><button>.
        // `ret` wird auf den Prefix eingegrenzt, damit kein Open-Redirect entsteht.
        group.MapPost("/lang", async (HttpContext ctx) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            // Form-Body lesen (wie die anderen POST-Handler hier): Minimal-APIs binden
            // einfache Parameter sonst aus dem Query-String, nicht dem Form-Body.
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var form = await ctx.Request.ReadFormAsync();
            var set = form["set"].ToString();
            var ret = form["ret"].ToString();
            var lang = set is "de" or "en" or "fr" ? set : HeimdallI18n.DefaultLang;
            // ret kommt vom Nav-Formular und trägt den externen Prefix (BasePath);
            // Open-Redirect-Grenze ist folgerichtig full, nicht der In-App-Prefix.
            var retUrl = string.IsNullOrEmpty(ret) || !ret.StartsWith(full, StringComparison.Ordinal) ? full : ret;
            ctx.Response.Cookies.Append("heimdall-lang", lang, new CookieOptions
            {
                MaxAge = TimeSpan.FromDays(365),
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = false,   // allows future client-side switcher; harmless either way
                Path = "/",
            });
            return Results.Redirect(retUrl);
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
    /// Bestimmt den Zurück-Link zum zuvor angesehenen Dashboard aus dem Referer-
    /// Header. Nur interne Dashboard-Routen werden akzeptiert (Open-Redirect-
    /// Schutz): der Pfad muss unter <c>{prefix}/dashboards/</c> liegen, das
    /// extrahierte UID-Segment alphanumerisch sein und vom aktuellen UID
    /// abweichen. Sonst null (kein Back-Link; der Browser-Back bleibt Reserve).
    /// </summary>
    private static string? ResolveBackUrl(HttpContext ctx, string prefix, string uid)
    {
        var referer = ctx.Request.Headers.Referer.ToString();
        if (string.IsNullOrEmpty(referer)) return null;
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var uri)) return null;
        // Der externe Referer-Pfad trägt die PathBase (IIS-Unterverzeichnis/
        // Proxy-Pfad-Strip) — mit FullPrefix vergleichen, sonst schlägt der
        // Prefix-Check unter PathBase-Deployment immer fehl (Back-Link nie da).
        var path = uri.AbsolutePath;
        var dashRoot = HeimdallUiPaths.FullPrefix(ctx, prefix) + "/dashboards/";
        if (!path.StartsWith(dashRoot, StringComparison.Ordinal)) return null;
        var rest = path.Substring(dashRoot.Length);
        var seg = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(seg) || seg == "import" || seg == uid) return null;
        // Nur Dashboard-UID-artige Segmente zulassen (kein Open-Redirect-Vektor).
        foreach (var ch in seg)
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')) return null;
        return dashRoot + seg;
    }

    /// <summary>
    /// CSRF-Schutz für die zustandsändernden POST-Endpoints (Login/Logout,
    /// Dashboard-Import/Delete, Alert-Save/Delete). Bei Basic-Auth werden Credentials
    /// cross-site automatisch mitgesendet — der Angreifer kann die Response zwar nicht
    /// lesen (SOP), aber zustandsändernde POSTs wären ohne diesen Check möglich.
    /// Origin/Referer-Check ist der OWASP-empfohlene, JavaScript-freie Schutz für
    /// nicht-Cookie-Auth-UIs: ein Cross-Site-Form-POST setzt einen anderen Origin-Header
    /// (oder keinen), den der Browser nicht fälschen kann. Same-Site-Requests (leerer
    /// Origin bei GET-Form-Navigation, gleicher Host bei POST) werden akzeptiert.
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
            return SameAuthority(refUri, ctx);
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return SameAuthority(uri, ctx);
    }

    /// <summary>
    /// Authority-Vergleich (Host + Port) zwischen dem externen Origin/Referer-URI und
    /// dem Request — mit Port-Normalisierung: Der Browser lässt Default-Ports (80/443)
    /// im Origin/Referer weg, und der Host-Header trägt den Port bei Default-Ports
    /// ebenfalls nicht (<c>HostString.Port == null</c>). Ein roher Port-Vergleich
    /// (443 vs. null) würde legitime Same-Origin-POSTs fälschlich als Cross-Origin
    /// zurückweisen — z. B. den Login unter IIS auf Default-Port. Hinter
    /// TLS-terminierendem Proxy/IIS-ARR gilt X-Forwarded-Host/-Proto als externe
    /// Authority; null-Ports (beidseitig Default des jeweiligen Schemas) gelten als
    /// gleich.
    /// </summary>
    private static bool SameAuthority(Uri external, HttpContext ctx)
    {
        var fwdHost = ctx.Request.Headers["X-Forwarded-Host"].ToString();
        var first = fwdHost.Split(',')[0].Trim();
        var authority = first.Length > 0 ? new HostString(first) : ctx.Request.Host;
        if (!string.Equals(external.Host, authority.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        int? externalPort = external.IsDefaultPort ? null : external.Port;
        int? requestPort = authority.Port;
        var fwdProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
        var scheme = fwdProto.Split(',')[0].Trim();
        if (scheme.Length == 0) scheme = ctx.Request.Scheme;
        if (requestPort is int rp && IsDefaultPort(scheme, rp)) requestPort = null;
        return Nullable.Equals(externalPort, requestPort);
    }

    private static bool IsDefaultPort(string scheme, int port) =>
        (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && port == 80) ||
        (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && port == 443);
}