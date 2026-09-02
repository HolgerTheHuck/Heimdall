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
    /// <summary>
    /// Stampft <c>Cache-Control: no-store</c> auf alle dynamischen Antworten, die
    /// die Pipeline weiter unten erzeugt. Heimdall sendete bisher GAR KEINEN
    /// Cache-Header — exakt das lädt Middle-Boxen (IIS Output Caching, ARR-Cache,
    /// heuristisches Browser-Caching) ein, Panel-/API-Antworten eigenmächtig zu
    /// cachen. Symptom in Produktion: importierte Dashboard-Panels blieben bei
    /// Presets < 24 h auf alten Zeitfenstern „eingefroren“, weil der vorgelagerte
    /// IIS die byte-identische Panel-URL (preset + vars sind stabil) aus seinem
    /// Cache bediente — samt dem zum Cache-Zeitpunkt berechneten <c>to</c> —,
    /// während nie angefragte URLs (7 t) frische Antworten bekamen. Browser-
    /// seitiges „Disable cache“ hilft dagegen nicht (der Cache sitzt hinter dem
    /// Browser). <c>no-store</c> weist jede Cache-Schicht (inkl. IIS-User-Mode-
    /// Cache) an, die Antwort nicht zu speichern.
    ///
    /// Nach <c>UseStaticFiles()</c> einhängen — statische Assets (CSS/JS/Fonts)
    /// laufen am Middleware-Kurzschluss vorbei und behalten ihr eigenes
    /// Caching-Verhalten. Header werden VOR <c>next()</c> gesetzt, sodass sie für
    /// jede weiter unten beginnende Antwort gelten.
    /// </summary>
    public static void UseHeimdallNoCache(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            await next(ctx);
        });
    }

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

        group.MapGet("/traces", (HttpContext ctx, string? name, string[]? svc, string? ver, string? err, string? limit, string? offset, string? sort, string? dir, string? preset, string? from, string? to) =>
            new RazorComponentResult<TracesPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
                NameContains = name,
                // Chips schicken pro gewaehltem Service ein wiederholtes svc= mit
                // (leeres svc= bei „alle" wird elementweise weggesanitaert).
                ServiceNames = ParseSvcList(svc),
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

        group.MapGet("/logs", (HttpContext ctx, string? text, string? q, string? sev, string[]? svc, string? ver, string? limit, string? expand, string? offset, string? sort, string? dir, string? preset, string? from, string? to, string? view) =>
            new RazorComponentResult<LogsPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix),
                Text = text,
                Query1 = q,
                MinSeverity = ParseInt(sev),
                // Chips schicken pro gewaehltem Service ein wiederholtes svc= mit;
                // leere Werte („alle") werden elementweise weggesanitaert, sonst
                // wuerde ein leerer String als Filterwert interpretiert.
                ServiceNames = ParseSvcList(svc),
                ServiceVersion = NullIfEmpty(ver ?? ""),
                Limit = ParseInt(limit) ?? 200,
                Expand = expand == "1",
                Offset = ParseInt(offset) ?? 0,
                Sort = sort,
                Dir = dir,
                Preset = preset,
                From = ParseNs(from),
                To = ParseNs(to),
                // Alternative Ansicht: view=svc gruppiert die Liste nach Service.
                View = NullIfEmpty(view ?? ""),
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
            // Edit-Link direkt am Panel (Grafana-artig): Pfad-Key aus dem Match
            // Render-Panels ↔ rohes JSON; kein Treffer → kein Link.
            string? editUrl = null;
            var rawJson = store.GetRaw(uid);
            if (rawJson is not null)
            {
                var keys = GrafanaDashboardEditor.MatchRenderKeys(rawJson, new[] { slot.Panel });
                if (keys.Count > 0 && keys[0] is string k)
                    editUrl = HeimdallUiPaths.FullPrefix(ctx, prefix) + "/dashboards/" + Uri.EscapeDataString(uid) + "/panel/" + k + "/edit";
            }
            return new RazorComponentResult<GrafanaPanelFragment>(new { Panel = rp, BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), EditUrl = editUrl });
        });

        group.MapPost("/dashboards/{uid}/delete", (HttpContext ctx, string uid, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            store.Delete(uid);
            return Results.Redirect(HeimdallUiPaths.FullPrefix(ctx, prefix) + "/dashboards");
        });

        // === Dashboard-Editor (Erstellen/Bearbeiten, Formular + rohes JSON) ===
        // Backend = GrafanaDashboardEditor: JsonNode-Mutation auf dem ROHEN JSON —
        // verlustfrei fuer importierte Dashboards (datasource.uid, overrides, options
        // ueberleben). Panel-Identitaet sind Pfad-Keys ins panels-Array ("3" bzw.
        // "1.3" fuer Row-Kind-Panels) — nicht GrafanaPanel.Id und nicht der
        // Slot-Index der Ansicht. Alle POSTs: Same-Origin-Check + Redirect-Muster
        // wie /dashboards/import; Save IMMER mit Routen-Uid (store.Save(uid, json)),
        // damit uid-lose Alt-Bestände ihren Dateinamen behalten.
        group.MapGet("/dashboards/new", (HttpContext ctx) =>
            new RazorComponentResult<GrafanaDashboardEditPage>(new
            { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix) }));

        group.MapGet("/dashboards/{uid}/edit", (HttpContext ctx, string uid, string? err) =>
            new RazorComponentResult<GrafanaDashboardEditPage>(new
            { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Uid = uid, Error = err }));

        group.MapGet("/dashboards/{uid}/json", (HttpContext ctx, string uid, string? err) =>
            new RazorComponentResult<GrafanaDashboardJsonPage>(new
            { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Uid = uid, Error = err }));

        // "+ Target"/"+ Schwelle"-Links addieren Server-seitig Formularzeilen
        // (addtgt= Anzahl zusaetzlicher Target-Zeilen, addthr analog). Kein JS.
        group.MapGet("/dashboards/{uid}/panel/new", (HttpContext ctx, string uid, string? addtgt, string? addthr) =>
            new RazorComponentResult<GrafanaPanelEditPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Uid = uid,
                AddTgt = ParseInt(addtgt), AddThr = ParseInt(addthr),
            }));

        group.MapGet("/dashboards/{uid}/panel/{key}/edit", (HttpContext ctx, string uid, string key, string? addtgt, string? addthr, string? err) =>
            new RazorComponentResult<GrafanaPanelEditPage>(new
            {
                BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Uid = uid, PanelKey = key,
                AddTgt = ParseInt(addtgt), AddThr = ParseInt(addthr), Error = err,
            }));

        // Live-Vorschau: der „Vorschau"-Button im Panel-Formular (GET-Submit,
        // formaction hierher) liefert ALLE Felder in der Query — das Panel wird
        // aus dem UNGESPEICHERTEN Stand ausgewertet (Panel-JSON auf einem
        // Wegwerf-Skeleton gebaut, nicht gespeichert) und inline unter dem
        // Formular gerendert. GET = kein CSRF-Risiko, nichts persistiert.
        group.MapGet("/dashboards/{uid}/panel/preview", (HttpContext ctx, string uid, string? preset, string? from, string? to) =>
        {
            string? err = null;
            var store = ctx.RequestServices.GetRequiredService<IGrafanaDashboardStore>();
            var dash = store.Get(uid);
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            if (dash is null) return Results.NotFound();
            var pf = BindPanelQuery(ctx.Request.Query);
            var panelKey = NullIfEmpty(ctx.Request.Query["panelKey"].ToString());

            // Panel aus dem Formularstand bauen (Wegwerf-Skeleton, nie gespeichert).
            // UpsertPanel lehnt z. B. fehlende Targets ab — der Fehler landet als
            // Meldung über dem Formular statt als Preview.
            RenderedPanel? preview = null;
            try
            {
                var skeleton = GrafanaDashboardEditor.CreateNew("preview");
                var json = GrafanaDashboardEditor.UpsertPanel(skeleton, null, pf);
                var parsed = GrafanaDashboardModel.Parse(json);
                var panel = parsed?.Panels.Count > 0 ? parsed.Panels[0] : null;
                if (panel is not null)
                {
                    var engine = ctx.RequestServices.GetRequiredService<Heimdall.Prometheus.PromEngine>();
                    var query = ctx.RequestServices.GetRequiredService<Heimdall.IHeimdallQuery>();
                    var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
                    // Render-Variablen des ECHTEN Dashboards (Template-Variablen +
                    // $__rate_interval &c. aus den var-*/Zeit-Params), damit die
                    // Vorschau dieselbe Interpolation nutzt wie die Ansicht.
                    var vars = ctx.Request.Query
                        .Where(kv => kv.Key.StartsWith("var-", StringComparison.Ordinal))
                        .ToDictionary(kv => kv.Key.Substring(4), kv => kv.Value.ToString(), StringComparer.Ordinal);
                    var prep = GrafanaDashboardRender.BuildRenderVars(dash, vars, preset, ParseNs(from), ParseNs(to), HeimdallRange.NowUnixNano());
                    preview = GrafanaPanelRenderer.Render(panel, engine, prep.FromMs, prep.ToMs, prep.StepMs, prep.RenderVars, query, i18n.Lang, full);
                }
            }
            catch (ArgumentException ex)
            {
                err = ex.Message;   // z. B. fehlende Targets — Seite mit Meldung + Formularstand
            }

            return new RazorComponentResult<GrafanaPanelEditPage>(new
            {
                BasePath = full, Uid = uid, PanelKey = panelKey,
                Form = pf, Preview = preview, Error = err,
            });
        });

        group.MapGet("/dashboards/{uid}/var/new", (HttpContext ctx, string uid) =>
            new RazorComponentResult<GrafanaVariableEditPage>(new
            { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Uid = uid }));

        group.MapGet("/dashboards/{uid}/var/{key}/edit", (HttpContext ctx, string uid, string key, string? err) =>
            new RazorComponentResult<GrafanaVariableEditPage>(new
            { BasePath = HeimdallUiPaths.FullPrefix(ctx, prefix), Uid = uid, VarKey = key, Error = err }));

        group.MapPost("/dashboards/save", async (HttpContext ctx, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var form = await ctx.Request.ReadFormAsync();
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
            var uid = NullIfEmpty(form["uid"].ToString());
            var title = form["title"].ToString().Trim();
            try
            {
                if (string.IsNullOrEmpty(uid))
                {
                    // Neu-Anlage: Skeleton mit generierter Uid; Save leitet die Uid
                    // aus dem Payload ab und legt die Datei an.
                    if (title.Length == 0)
                        return Results.Redirect($"{full}/dashboards/new?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.dashboardtitle")));
                    var uid2 = store.Save(GrafanaDashboardEditor.CreateNew(title));
                    return Results.Redirect($"{full}/dashboards/{uid2}");
                }
                // Rename: Uid (und damit die Datei) bleibt — Titel nur via SetTitle.
                var raw = store.GetRaw(uid);
                if (raw is null) return Results.Redirect($"{full}/dashboards?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.notfound")));
                store.Save(uid, GrafanaDashboardEditor.SetTitle(raw, title));
                return Results.Redirect($"{full}/dashboards/{uid}");
            }
            catch (ArgumentException)
            {
                return Results.Redirect(string.IsNullOrEmpty(uid)
                    ? $"{full}/dashboards/new?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.dashboardtitle"))
                    : $"{full}/dashboards/{uid}/edit?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.dashboardtitle")));
            }
        });

        group.MapPost("/dashboards/{uid}/duplicate", (HttpContext ctx, string uid, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var raw = store.GetRaw(uid);
            if (raw is null) return Results.Redirect($"{full}/dashboards?err=" + Uri.EscapeDataString("not found"));
            var newUid = store.Save(GrafanaDashboardEditor.Duplicate(raw, GrafanaDashboardEditor.NewUid()));
            return Results.Redirect($"{full}/dashboards/{newUid}");
        });

        group.MapPost("/dashboards/{uid}/json", async (HttpContext ctx, string uid, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var form = await ctx.Request.ReadFormAsync();
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
            var content = form["json"].ToString();
            if (string.IsNullOrWhiteSpace(content))
                return Results.Redirect($"{full}/dashboards/{uid}/json?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.nodashboardjson")));
            try
            {
                // ReplaceJson erzwingt die Routen-Uid (eine andere Uid im Text wuerde
                // sonst als neue Datei speichern und die Alt-Datei verwaisten).
                store.Save(uid, GrafanaDashboardEditor.ReplaceJson(content, uid));
                return Results.Redirect($"{full}/dashboards/{uid}");
            }
            catch (ArgumentException ex)
            {
                return Results.Redirect($"{full}/dashboards/{uid}/json?err=" + Uri.EscapeDataString(ex.Message));
            }
        });

        group.MapPost("/dashboards/{uid}/panel/save", async (HttpContext ctx, string uid, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var form = await ctx.Request.ReadFormAsync();
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
            var panelKey = NullIfEmpty(form["panelKey"].ToString());
            var errUrl = $"{full}/dashboards/{uid}/panel/{(panelKey is null ? "new" : panelKey + "/edit")}?err=";
            var pf = BindPanelForm(form);
            if (string.IsNullOrWhiteSpace(pf.Title))
                return Results.Redirect(errUrl + Uri.EscapeDataString(i18n.T("endpoint.err.paneltitle")));
            if (!string.Equals(pf.Type, "row", StringComparison.OrdinalIgnoreCase) && pf.Targets.All(t => string.IsNullOrWhiteSpace(t.Expr)))
                return Results.Redirect(errUrl + Uri.EscapeDataString(i18n.T("endpoint.err.paneltargets")));
            try
            {
                var raw = store.GetRaw(uid);
                if (raw is null) return Results.NotFound();
                // Grid-Hygiene: überlappende Panels = unlesbares Dashboard. Ohne
                // ausdrücklichen Haken (force=1) blockt ein Kollisions-Save mit
                // dem Titel des kollidierenden Panels (Update: eigenes Panel ausgenommen).
                if (form["force"] != "1")
                {
                    var hit = FindOverlap(raw, panelKey, pf);
                    if (hit is not null)
                        return Results.Redirect(errUrl + Uri.EscapeDataString(
                            i18n.T("endpoint.err.overlap", hit)));
                }
                store.Save(uid, GrafanaDashboardEditor.UpsertPanel(raw, panelKey, pf));
                return Results.Redirect($"{full}/dashboards/{uid}");
            }
            catch (Exception ex)
            {
                return Results.Redirect(errUrl + Uri.EscapeDataString(ex.Message));
            }
        });

        group.MapPost("/dashboards/{uid}/panel/{key}/delete", (HttpContext ctx, string uid, string key, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            try
            {
                var raw = store.GetRaw(uid);
                if (raw is not null) store.Save(uid, GrafanaDashboardEditor.DeletePanel(raw, key));
                return Results.Redirect($"{full}/dashboards/{uid}/edit");
            }
            catch (ArgumentException ex)
            {
                return Results.Redirect($"{full}/dashboards/{uid}/edit?err=" + Uri.EscapeDataString(ex.Message));
            }
        });

        group.MapPost("/dashboards/{uid}/var/save", async (HttpContext ctx, string uid, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var form = await ctx.Request.ReadFormAsync();
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            var i18n = ctx.RequestServices.GetRequiredService<Heimdall.Blazor.IHeimdallI18n>();
            var varKey = NullIfEmpty(form["varKey"].ToString());
            var errUrl = $"{full}/dashboards/{uid}/var/{(varKey is null ? "new" : varKey + "/edit")}?err=";
            var vf = BindVariableForm(form);
            if (string.IsNullOrWhiteSpace(vf.Name))
                return Results.Redirect(errUrl + Uri.EscapeDataString(i18n.T("endpoint.err.varname")));
            try
            {
                var raw = store.GetRaw(uid);
                if (raw is null) return Results.Redirect($"{full}/dashboards?err=" + Uri.EscapeDataString(i18n.T("endpoint.err.notfound")));
                store.Save(uid, GrafanaDashboardEditor.UpsertVariable(raw, varKey, vf));
                return Results.Redirect($"{full}/dashboards/{uid}");
            }
            catch (Exception ex)
            {
                return Results.Redirect(errUrl + Uri.EscapeDataString(ex.Message));
            }
        });

        group.MapPost("/dashboards/{uid}/var/{key}/delete", (HttpContext ctx, string uid, string key, IGrafanaDashboardStore store) =>
        {
            if (!CheckSameOrigin(ctx, prefix)) return Results.BadRequest("cross-origin POST rejected");
            var full = HeimdallUiPaths.FullPrefix(ctx, prefix);
            try
            {
                var raw = store.GetRaw(uid);
                if (raw is not null) store.Save(uid, GrafanaDashboardEditor.DeleteVariable(raw, key));
                return Results.Redirect($"{full}/dashboards/{uid}/edit");
            }
            catch (ArgumentException ex)
            {
                return Results.Redirect($"{full}/dashboards/{uid}/edit?err=" + Uri.EscapeDataString(ex.Message));
            }
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

        // Fail-Fast statt Lazy-500: Die /alerts-POSTs deklarieren IAlertRuleStore/
        // IAlertStateStore als Handler-Parameter, die AlertsPage @inject-ed beide +
        // HeimdallAlertingOptions. Fehlen diese Dienste im Container, wirft die
        // RequestDelegateFactory erst beim ERSTEN Request („Failure to infer one
        // or more parameters") — und reißt die komplette Routing-Tabelle mit: kein
        // Endpunkt des Hosts antwortet mehr, auch komplett unbeteiligte. Hier
        // sofort werfen, mit Handlungsanweisung (Startzeit-Fehler statt Lazy-500).
        // Normalfall: AddHeimdallDashboard registriert die Defaults — nur wer
        // MapHeimdallDashboard OHNE AddHeimdallDashboard aufruft, landet hier.
        if (endpoints.ServiceProvider.GetService<IAlertRuleStore>() is null ||
            endpoints.ServiceProvider.GetService<IAlertStateStore>() is null)
            throw new InvalidOperationException(
                "Heimdall dashboard maps /alerts endpoints that require IAlertRuleStore and " +
                "IAlertStateStore. Call AddHeimdallDashboard(sink) (registers defaults) or " +
                "AddHeimdallAlerting(...) — otherwise routing initialization fails at first " +
                "request with 'Failure to infer one or more parameters' for every route of " +
                "the host.");

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

    /// <summary>Service-Multi-Select (Chips): wiederholte <c>svc=a&amp;svc=b</c>-
    /// Query-Params binden als Array. Elementweise Sanitierung — leere Strings
    /// (Chip-„alle"-Submit) fallen raus, Duplikate ebenso; alles leer = kein
    /// Filter = alle Services.</summary>
    private static System.Collections.Generic.IReadOnlyList<string>? ParseSvcList(string[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        System.Collections.Generic.List<string> result = new(raw.Length);
        foreach (var s in raw)
        {
            var v = NullIfEmpty(s ?? "");
            if (v is not null && !result.Contains(v)) result.Add(v);
        }
        return result.Count == 0 ? null : result;
    }

    private static int? ParseInt(Microsoft.Extensions.Primitives.StringValues v) =>
        int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long? ParseLong(Microsoft.Extensions.Primitives.StringValues v) =>
        long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Bindet das Panel-Formular (indexbasierte Target-/Threshold-Zeilen
    /// t{i}Expr/t{i}Legend/t{i}Instant bzw. thr{i}Value/thr{i}Color — Checkboxen
    /// verschieben sonst die Parallel-Array-Zuordnung). Blanks leert der Editor
    /// beim Speichern heraus; Zeilen-Caps schuetzen vor Formular-Bomben.</summary>
    private static GrafanaDashboardEditor.PanelForm BindPanelForm(Microsoft.AspNetCore.Http.IFormCollection form) =>
        BindPanelFields(k => form[k].ToString());

    /// <summary>Titel des ersten Panels, das das Formular-Rechteck schneidet
    /// (Update: das Panel am panelKey selbst ist ausgenommen); null = kollisionsfrei.</summary>
    private static string? FindOverlap(string rawJson, string? panelKey, GrafanaDashboardEditor.PanelForm pf)
    {
        var panels = GrafanaDashboardEditor.ListPanels(rawJson);
        foreach (var p in panels)
        {
            if (panelKey is not null && string.Equals(p.Key, panelKey, StringComparison.Ordinal)) continue;
            var g = p.GridPos;
            if (pf.X < g.X + g.W && g.X < pf.X + Math.Max(1, pf.W)
                && pf.Y < g.Y + g.H && g.Y < pf.Y + Math.Max(1, pf.H))
                return string.IsNullOrEmpty(p.Title) ? p.Key : p.Title;
        }
        return null;
    }

    /// <summary>Panel-Formular aus GET-Query (Live-Vorschau: derselbe Feld-Naming-
    /// Vertrag wie das POST-Formular — t{i}Expr/thr{i}* inkl. Entfernen-Haken t{i}Rm).</summary>
    private static GrafanaDashboardEditor.PanelForm BindPanelQuery(Microsoft.AspNetCore.Http.IQueryCollection query) =>
        BindPanelFields(k => query[k].ToString());

    /// <summary>Panel-Formular aus Name-Wert-Zugriff (IFormCollection und IQueryCollection
    /// teilen denselben Feld-Naming-Vertrag, daher generiert über einen Getter).</summary>
    private static GrafanaDashboardEditor.PanelForm BindPanelFields(Func<string, string> get)
    {
        const int MaxRows = 50;
        int tgtCount = Math.Clamp(ParseInt(get("tgtCount")) ?? 0, 0, MaxRows);
        var targets = new System.Collections.Generic.List<GrafanaDashboardEditor.TargetForm>(tgtCount);
        for (int i = 0; i < tgtCount; i++)
        {
            if (get($"t{i}Rm") == "1") continue;   // Entfernen-Haken: Zeile fällt weg (Save + Vorschau)
            targets.Add(new GrafanaDashboardEditor.TargetForm(
                get($"t{i}Expr"),
                NullIfEmpty(get($"t{i}Legend")),
                get($"t{i}Instant") == "1"));
        }
        int thrCount = Math.Clamp(ParseInt(get("thrCount")) ?? 0, 0, MaxRows);
        var thresholds = new System.Collections.Generic.List<GrafanaDashboardEditor.ThresholdForm>(thrCount);
        for (int i = 0; i < thrCount; i++)
        {
            if (get($"thr{i}Rm") == "1") continue;
            var raw = get($"thr{i}Value");
            double? val = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            thresholds.Add(new GrafanaDashboardEditor.ThresholdForm(val, get($"thr{i}Color")));
        }
        return new GrafanaDashboardEditor.PanelForm(
            get("title"),
            NullIfEmpty(get("type")) ?? "timeseries",
            ParseInt(get("gridX")) ?? 0, ParseInt(get("gridY")) ?? 0,
            ParseInt(get("gridW")) ?? 6, ParseInt(get("gridH")) ?? 8,
            targets,
            NullIfEmpty(get("unit")),
            thresholds,
            NullIfEmpty(get("repeat")),
            NullIfEmpty(get("graphMode")));
    }

    /// <summary>Bindet das Variablen-Formular (Render-Contract-Form, siehe Editor).</summary>
    private static GrafanaDashboardEditor.VariableForm BindVariableForm(Microsoft.AspNetCore.Http.IFormCollection form) =>
        new(
            form["name"].ToString(),
            NullIfEmpty(form["type"].ToString()) ?? "query",
            NullIfEmpty(form["query"].ToString()) ?? string.Empty,
            NullIfEmpty(form["current"].ToString()),
            form["includeAll"] == "1",
            form["multi"] == "1");

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
    /// Zusätzlich akzeptiert die Liste <c>Heimdall:Ui:TrustedOrigins</c> externe
    /// Origins, unter denen die UI hinter einem Reverse-Proxy/ARR mit abweichender
    /// TLD betrieben wird und deren Host-Header der Proxy nicht 1:1 durchreicht
    /// (X-Forwarded-Host fehlt) — siehe <see cref="IsTrustedOrigin"/>.
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
            return SameAuthority(refUri, ctx) || IsTrustedOrigin(refUri, ctx);
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return SameAuthority(uri, ctx) || IsTrustedOrigin(uri, ctx);
    }

    /// <summary>
    /// Zusätzlicher Trust-Anchor für <see cref="CheckSameOrigin"/>: die Sektion
    /// <c>Heimdall:Ui:TrustedOrigins</c> (String-Array vollständiger Origins, z. B.
    /// <c>"https://portal.example.de"</c>) listet die externen Origins, unter denen
    /// die UI hinter einem Reverse-Proxy erreichbar ist. Nötig, wenn der Proxy die
    /// externe Authority nicht durchreicht: Der Browser sendet beim Form-POST
    /// unausweisbar Origin/Referer mit der EXTERNEN Origin, während
    /// <see cref="SameAuthority"/> gegen Request.Host (bzw. X-Forwarded-Host) läuft —
    /// bei abweichender TLD und fehlendem X-Forwarded-Host (IIS-ARR setzt das nicht
    /// von selbst) schlägt der Vergleich fehl, obwohl der Request legitime
    /// Same-Origin-Navigation des externen Frontends ist. Scheme und Host werden
    /// case-insensitive verglichen, Ports wie in <see cref="SameAuthority"/>
    /// (Default-Ports gelten als null).
    /// </summary>
    private static bool IsTrustedOrigin(Uri external, HttpContext ctx)
    {
        // Bewusst ohne Configuration.Binder (Get<string[]>()): die Sektion wird
        // per GetChildren() gelesen — Heimdall.Blazor referenziert das Binder-
        // Paket nicht, und für ein String-Array reicht GetChildren().
        var section = ctx.RequestServices
            .GetService<Microsoft.Extensions.Configuration.IConfiguration>()
            ?.GetSection("Heimdall:Ui:TrustedOrigins");
        if (section is null) return false;
        foreach (var child in section.GetChildren())
        {
            var entry = child.Value;
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var t)) continue;
            if (!string.Equals(t.Host, external.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(t.Scheme, external.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
            int? tPort = t.IsDefaultPort ? null : t.Port;
            int? ePort = external.IsDefaultPort ? null : external.Port;
            if (Nullable.Equals(tPort, ePort)) return true;
        }
        return false;
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