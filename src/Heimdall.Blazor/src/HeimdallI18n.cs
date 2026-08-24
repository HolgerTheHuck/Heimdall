using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace Heimdall.Blazor;

// ---------------------------------------------------------------------------
// Heimdall-i18n: umschaltbare UI-Sprache (de/en/fr).
//
// Zwei Konsumenten teilen sich EINE Tabelle:
//   1. UI (statisches SSR, per-Request): der scoped IHeimdallI18n-Service liest
//      die Sprache aus dem `heimdall-lang`-Cookie (Fallback ?lang=-Query,
//      Accept-Language, DefaultLang).
//   2. Asynchrone Alert-Kanäle (AlertEvaluator = Singleton-HostedService, kein
//      HttpContext): rufen HeimdallI18n.T(lang, key) direkt mit der aus
//      HeimdallAlertingOptions.Language konfigurierten Sprache auf.
//
// Daher: Tabelle = statische, dependency-freie Datenhaltung; der scoped Service
// ist ein dünner Wrapper, der nur die Request-Sprache resolved und delegiert.
//
// Key-Konvention: dotted lowercase (nav.overview, page.title.home, alert.state.firing).
// Fallback-Reihenfolge in T(lang,key): lang -> de -> key selbst (wirft nie).
//
// WICHTIG: die de-Werte reproduzieren die historischen deutschen UI-Literale
// wortwörtlich, damit bestehende UI-Tests (HeimdallUiTests/AlertsPageTests)
// ohne Anpassung grün bleiben.
// ---------------------------------------------------------------------------

/// <summary>Statische Übersetzungstabelle + Lookup (HttpContext-unabhängig nutzbar).</summary>
public static class HeimdallI18n
{
    /// <summary>Default-Sprache, wenn weder Cookie noch Query noch Accept-Language greifen.</summary>
    public const string DefaultLang = "de";

    /// <summary>Unterstützte Sprachen (auch Reihenfolge im Flaggen-Schalter).</summary>
    public static readonly IReadOnlyCollection<string> Languages = new[] { "de", "en", "fr" };

    private static readonly Dictionary<string, Dictionary<string, string>> _table = new(256, StringComparer.Ordinal)
    {
        // --- Nav -------------------------------------------------------------
        ["nav.aria.label"]      = Lang("Hauptnavigation", "Main navigation", "Navigation principale"),
        ["nav.skip"]            = Lang("Zum Hauptinhalt springen", "Skip to main content", "Aller au contenu principal"),
        ["nav.overview"]        = Lang("Übersicht", "Overview", "Vue d'ensemble"),
        ["nav.monitoring"]      = Lang("Monitoring", "Monitoring", "Surveillance"),
        ["nav.dashboards"]      = Lang("Dashboards", "Dashboards", "Tableaux de bord"),
        ["nav.endpoints"]       = Lang("Endpoints", "Endpoints", "Endpoints"),
        ["nav.drilldown"]       = Lang("Drilldown", "Drilldown", "Exploration"),
        ["nav.alerts"]          = Lang("Alerts", "Alerts", "Alertes"),

        // --- Flaggen-Schalter ------------------------------------------------
        ["nav.flag.group"]      = Lang("Sprache wählen", "Choose language", "Choisir la langue"),
        ["nav.flag.de"]         = Lang("Deutsch", "German", "Allemand"),
        ["nav.flag.en"]         = Lang("English", "English", "Anglais"),
        ["nav.flag.fr"]         = Lang("Français", "French", "Français"),

        // --- Footer ----------------------------------------------------------
        ["footer.spans"]        = Lang("Spans", "Spans", "Spans"),
        ["footer.logs"]         = Lang("Logs", "Logs", "Logs"),
        ["footer.metrics"]      = Lang("Metriken", "Metrics", "Métriques"),

        // --- Seitentitel (<title>/<h1>) -------------------------------------
        ["page.title.home"]           = Lang("Übersicht", "Overview", "Vue d'ensemble"),
        ["page.title.drilldown"]      = Lang("Drilldown", "Drilldown", "Exploration"),
        ["page.title.traces"]         = Lang("Traces", "Traces", "Traces"),
        ["page.title.trace"]          = Lang("Trace", "Trace", "Trace"),
        ["page.title.logs"]           = Lang("Logs", "Logs", "Logs"),
        ["page.title.metrics"]        = Lang("Metriken", "Metrics", "Métriques"),
        ["page.title.monitoring"]     = Lang("Monitoring", "Monitoring", "Surveillance"),
        ["page.title.endpoints"]      = Lang("Endpoints", "Endpoints", "Endpoints"),
        ["page.title.login"]          = Lang("Login", "Sign in", "Connexion"),
        ["page.title.alerts"]         = Lang("Alerts", "Alerts", "Alertes"),
        ["page.title.alertRule"]      = Lang("Alarm-Regel", "Alert rule", "Règle d'alerte"),
        ["page.title.alertDetail"]    = Lang("Alarm-Regel", "Alert rule", "Règle d'alerte"),
        ["page.title.grafanaList"]    = Lang("Grafana-Dashboards", "Grafana dashboards", "Tableaux de bord Grafana"),
        ["page.title.grafanaImport"]  = Lang("Dashboard importieren", "Import dashboard", "Importer un tableau de bord"),

        // --- Home / Übersicht ------------------------------------------------
        ["home.subtitle"]           = Lang("Heimdall-Observability · Schnappschuss der aktuellen Datenlage", "Heimdall observability · snapshot of current data", "Heimdall observabilité · instantané des données actuelles"),
        ["home.empty.title"]        = Lang("Noch keine Telemetrie eingegangen", "No telemetry received yet", "Aucune télémétrie reçue pour l'instant"),
        ["home.empty.body"]         = Lang("Sende OpenTelemetry-Daten an Heimdall, dann erscheinen hier Health-KPIs, letzte Fehler-Traces und Logs.", "Send OpenTelemetry data to Heimdall and health KPIs, recent error traces and logs will appear here.", "Envoyez des données OpenTelemetry à Heimdall ; les KPI de santé, les traces d'erreur récentes et les logs apparaîtront ici."),
        ["home.empty.sample"]       = Lang("Beispiel-Senden via", "Send via", "Envoi via"),
        ["home.kpi.spans"]          = Lang("Spans", "Spans", "Spans"),
        ["home.kpi.logs"]           = Lang("Logs", "Logs", "Logs"),
        ["home.kpi.metrics"]        = Lang("Metrik-Punkte", "Metric points", "Points de métrique"),
        ["home.kpi.errtraces"]      = Lang("Fehler-Traces", "Error traces", "Traces d'erreur"),
        ["kpi.sub.total"]           = Lang("gesamt", "total", "total"),
        ["kpi.sub.latest"]          = Lang("neueste {0}", "newest {0}", "{0} récentes"),
        // --- Navcards (Home + Drilldown teilen sich diese) -------------------
        ["navcard.dashboard.t"]     = Lang("Dashboard", "Dashboard", "Tableau de bord"),
        ["navcard.dashboard.sub"]   = Lang("RED-Metriken, Latenzen, Uptime", "RED metrics, latency, uptime", "Métriques RED, latence, disponibilité"),
        ["navcard.traces.t"]        = Lang("Traces", "Traces", "Traces"),
        ["navcard.traces.sub"]      = Lang("Spans suchen und inspizieren", "Search and inspect spans", "Rechercher et inspecter les spans"),
        ["navcard.logs.t"]          = Lang("Logs", "Logs", "Logs"),
        ["navcard.logs.sub"]        = Lang("Volltext-Suche + Severity", "Full-text search + severity", "Recherche plein texte + sévérité"),
        ["navcard.metrics.t"]       = Lang("Metriken", "Metrics", "Métriques"),
        ["navcard.metrics.sub"]     = Lang("Zeitreihen & Histogramme", "Time series & histograms", "Séries temporelles & histogrammes"),
        ["navcard.endpoints.t"]     = Lang("Endpoints", "Endpoints", "Endpoints"),
        ["navcard.endpoints.sub"]   = Lang("Controller/Action-Drilldown", "Controller/action drilldown", "Drilldown contrôleur/action"),
        ["navcard.dashboards.t"]    = Lang("Dashboards", "Dashboards", "Tableaux de bord"),
        ["navcard.dashboards.sub"]  = Lang("Grafana-JSON importieren & rendern", "Import & render Grafana JSON", "Importer & rendre du JSON Grafana"),
        // --- Home-Panelüberschriften + Leer-Zustände ------------------------
        ["home.section.errtraces"]  = Lang("Neueste Fehler-Traces", "Recent error traces", "Traces d'erreur récentes"),
        ["home.section.errlogs"]    = Lang("Neueste Fehler-Logs", "Recent error logs", "Logs d'erreur récents"),
        ["empty.noErrTraces"]       = Lang("Keine Fehler-Traces — alle Spans im Status OK.", "No error traces — all spans OK.", "Aucune trace d'erreur — toutes les spans sont OK."),
        ["empty.noErrLogs"]         = Lang("Keine Fehler-Logs (Severity ≥ ERROR).", "No error logs (severity ≥ ERROR).", "Aucun log d'erreur (sévérité ≥ ERROR)."),

        // --- KPI-Tone (Text-Fallback für farbcodierte KPIs) ------------------
        ["kpi.tone.ok"]         = Lang("OK", "OK", "OK"),
        ["kpi.tone.warn"]       = Lang("Warnung", "Warning", "Avertissement"),
        ["kpi.tone.err"]        = Lang("Kritisch", "Critical", "Critique"),
        ["kpi.tone.accent"]     = Lang("Akzent", "Info", "Info"),

        // --- Pager -----------------------------------------------------------
        ["pager.label"]         = Lang("Seiten-Navigation", "Pagination", "Navigation de pagination"),
        ["pager.newer"]         = Lang("← neuer", "← newer", "← récents"),
        ["pager.older"]         = Lang("älter →", "older →", "anciens →"),

        // --- Tabellen-Spaltenheader (generisch, mehrere Tabellen teilen sich) -
        ["table.actions"]       = Lang("Aktionen", "Actions", "Actions"),
        ["table.trace.id"]      = Lang("Trace-ID", "Trace ID", "ID de trace"),
        ["table.start"]         = Lang("Start", "Start", "Début"),
        ["table.duration"]      = Lang("Dauer", "Duration", "Durée"),
        ["table.spans"]         = Lang("Spans", "Spans", "Spans"),
        ["table.time"]          = Lang("Zeit", "Time", "Heure"),
        ["table.sev"]           = Lang("Sev", "Sev", "Sev"),
        ["table.body"]          = Lang("Body", "Body", "Corps"),
        ["table.trace"]         = Lang("Trace", "Trace", "Trace"),
        // --- Sortierbare Tabellen-Spalten (klickbare Header) -----------------
        // {0} = Spaltenname. aria-sort-Attribut + Link-Tooltip.
        ["sort.aria"]            = Lang("Nach {0} sortieren", "Sort by {0}", "Trier par {0}"),
        ["sort.asc"]             = Lang("aufsteigend", "ascending", "croissant"),
        ["sort.desc"]            = Lang("absteigend", "descending", "décroissant"),

        // --- Zeitbereich-Steuerung (HeimdallTimeRange) -----------------------
        ["timerange.aria"]        = Lang("Zeitraum", "Time range", "Plage horaire"),
        ["timerange.custom"]      = Lang("Benutzerdefiniert", "Custom", "Personnalisé"),
        ["timerange.from"]        = Lang("von", "from", "de"),
        ["timerange.to"]          = Lang("bis", "to", "à"),
        ["timerange.hint"]        = Lang("Zeiten werden beim Laden in Unix-ns umgerechnet.", "Times are converted to Unix-ns on load.", "Les heures sont converties en Unix-ns au chargement."),
        ["timerange.refresh"]     = Lang("Auto-Refresh", "Auto-refresh", "Auto-rafraîchissement"),
        ["timerange.refresh.off"] = Lang("aus", "off", "désactivé"),

        // --- Paging (HeimdallPager) ------------------------------------------
        ["pager.newer"]           = Lang("neuer", "newer", "plus récent"),
        ["pager.older"]           = Lang("älter", "older", "plus ancien"),

        // --- Aktion-Links ----------------------------------------------------
        ["endpoint.action.open"] = Lang("Öffnen", "Open", "Ouvrir"),
        ["alert.action.open"]    = Lang("Öffnen", "Open", "Ouvrir"),

        // --- Alert-Zustände / Signale (UI + Mail) ---------------------------
        ["alert.state.firing"]   = Lang("Ausgelöst", "Firing", "Déclenché"),
        ["alert.state.pending"]  = Lang("Wartet", "Pending", "En attente"),
        ["alert.state.resolved"] = Lang("Behoben", "Resolved", "Résolu"),
        ["alert.state.ok"]       = Lang("OK", "OK", "OK"),
        ["alert.signal.metric"]  = Lang("Metrik", "Metric", "Métrique"),
        ["alert.signal.log"]     = Lang("Log", "Log", "Log"),
        ["alert.signal.trace"]   = Lang("Trace", "Trace", "Trace"),

        // --- Alert-Mail-Body -------------------------------------------------
        ["alert.mail.body.heading"] = Lang("Heimdall-Alarm:", "Heimdall alert:", "Alerte Heimdall :"),
        ["alert.mail.body.rule"]    = Lang("Regel", "Rule", "Règle"),
        ["alert.mail.body.signal"]  = Lang("Signal", "Signal", "Signal"),
        ["alert.mail.body.value"]   = Lang("Wert", "Value", "Valeur"),
        ["alert.mail.body.time"]    = Lang("Zeitpunkt", "Time", "Date"),
        ["alert.mail.body.note"]    = Lang("Hinweis", "Note", "Note"),
        ["alert.mail.body.link"]    = Lang("Im Heimdall-Dashboard ansehen", "View in Heimdall dashboard", "Voir dans le tableau de bord Heimdall"),

        // --- Endpoint-Fehlermeldungen (Redirect-Query) ----------------------
        ["login.error.badcreds"]        = Lang("Benutzername oder Passwort falsch", "Incorrect username or password", "Nom d'utilisateur ou mot de passe incorrect"),
        ["login.error.noauth"]          = Lang("Auth nicht aktiv", "Authentication not enabled", "Authentification non activée"),
        ["endpoint.err.nodashboardjson"] = Lang("Kein Dashboard-JSON", "No dashboard JSON", "Aucun JSON de tableau de bord"),
        ["endpoint.err.rulename"]       = Lang("Regelname fehlt", "Rule name missing", "Nom de règle manquant"),

        // --- Drilldown -------------------------------------------------------
        ["drilldown.subtitle"]   = Lang("Signal-Detailseiten — Spans, Logs und Metriken einzeln inspizieren", "Signal detail pages — inspect spans, logs and metrics individually", "Pages de détail des signaux — inspecter spans, logs et métriques individuellement"),

        // --- Traces-Seite ----------------------------------------------------
        ["traces.filter.name"]    = Lang("Name enthält", "Name contains", "Le nom contient"),
        ["traces.filter.service"] = Lang("Service", "Service", "Service"),
        ["traces.filter.status"]  = Lang("Status", "Status", "Statut"),
        ["traces.filter.limit"]   = Lang("Limit", "Limit", "Limite"),
        ["traces.filter.submit"]  = Lang("Filtern", "Filter", "Filtrer"),
        ["traces.status.all"]     = Lang("alle", "all", "toutes"),
        ["traces.status.err"]     = Lang("nur Fehler", "errors only", "erreurs uniquement"),
        ["traces.status.ok"]      = Lang("nur OK", "OK only", "OK uniquement"),
        ["traces.status.errbadge"]= Lang("✕ Fehler", "✕ Error", "✕ Erreur"),
        ["traces.status.okbadge"] = Lang("✓ OK", "✓ OK", "✓ OK"),
        ["traces.empty.more.title"]= Lang("Keine weiteren Traces", "No more traces", "Plus aucune trace"),
        ["traces.empty.more.body"] = Lang("In diesem Bereich gibt es keine älteren Traces mehr.", "There are no older traces in this range.", "Il n'y a plus de traces plus anciennes dans cette plage."),
        ["traces.empty.more.back"] = Lang("zurück zur neuesten Seite", "back to newest page", "retour à la page la plus récente"),
        ["traces.empty.title"]    = Lang("Keine Traces gefunden", "No traces found", "Aucune trace trouvée"),
        ["traces.empty.body"]     = Lang("Im gewählten Zeitraum liegen keine Spans vor. Zeitraum erweitern, Filter lockern oder Telemetrie senden.", "No spans in the selected time range. Widen the range, loosen filters or send telemetry.", "Aucune span dans la plage horaire sélectionnée. Élargissez la plage, assouplissez les filtres ou envoyez de la télémétrie."),
        ["traces.empty.hint.http"]= Lang("OTLP/HTTP", "OTLP/HTTP", "OTLP/HTTP"),
        ["traces.empty.hint.grpc"]= Lang("gRPC", "gRPC", "gRPC"),
        ["traces.count"]          = Lang("{0} auf dieser Seite · {1} Spans gesamt", "{0} on this page · {1} spans total", "{0} sur cette page · {1} spans au total"),

        // --- Allgemein (mehrere Seiten) --------------------------------------
        ["common.back.to"]   = Lang("zurück zur", "back to", "retour à la"),
        ["common.row.range"] = Lang("Zeile {0}–{1}", "Row {0}–{1}", "Ligne {0}–{1}"),
        ["table.status"]     = Lang("Status", "Status", "Statut"),

        // --- Metriken-Seite --------------------------------------------------
        ["metrics.filter.name"]        = Lang("Name", "Name", "Nom"),
        ["metrics.filter.limit"]       = Lang("Limit", "Limit", "Limite"),
        ["metrics.filter.load"]        = Lang("Laden", "Load", "Charger"),
        ["metrics.available.title"]    = Lang("Verfügbare Metriken", "Available metrics", "Métriques disponibles"),
        ["metrics.available.hint"]     = Lang("{0} Metrik-Punkte gesamt im Zeitraum. Namen wählen zum Anzeigen der Serie:", "{0} metric points total in the range. Pick a name to show its series:", "{0} points de métrique au total dans la plage. Choisissez un nom pour afficher sa série :"),
        ["metrics.empty.title"]        = Lang("Keine Metriken", "No metrics", "Aucune métrique"),
        ["metrics.empty.body"]         = Lang("Im gewählten Zeitraum liegen keine Metrik-Punkte vor. Zeitraum erweitern oder Metriken senden.", "No metric points in the selected time range. Widen the range or send metrics.", "Aucun point de métrique dans la plage sélectionnée. Élargissez la plage ou envoyez des métriques."),
        ["metrics.empty.series.title"] = Lang("Keine Messpunkte für „{0}\"", "No data points for \"{0}\"", "Aucun point de mesure pour « {0} »"),
        ["metrics.empty.series.body"]  = Lang("Zu diesem Metrik-Namen liegen im Zeitraum keine Punkte vor. Namen prüfen, Zeitraum erweitern oder Metriken senden.", "No points for this metric name in the range. Check the name, widen the range or send metrics.", "Aucun point pour ce nom de métrique dans la plage. Vérifiez le nom, élargissez la plage ou envoyez des métriques."),
        ["metrics.unit"]               = Lang("Einheit", "Unit", "Unité"),
        ["metrics.truncated.body"]     = Lang("Serie auf {0} Punkte begrenzt (Limit {1}).", "Series limited to {0} points (limit {1}).", "Série limitée à {0} points (limite {1})."),
        ["metrics.truncated.more"]     = Lang("mehr anzeigen (Limit {0})", "show more (limit {0})", "afficher plus (limite {0})"),
        ["metrics.distribution.title"] = Lang("Verteilung (letzter Punkt)", "Distribution (last point)", "Distribution (dernier point)"),
        ["chart.empty"]                = Lang("Keine Daten zum Zeichnen.", "No data to plot.", "Aucune donnée à tracer."),
        ["chart.yaxis"]                = Lang("Y-Achse:", "Y-axis:", "Axe Y :"),
        ["chart.aria.line"]            = Lang("Metrik-Zeitreihe", "Metric time series", "Série temporelle de métrique"),
        ["chart.aria.metric"]          = Lang("Metrik-Zeitreihe: {0}", "Metric time series: {0}", "Série temporelle de métrique : {0}"),
        ["chart.hist.empty"]           = Lang("Keine Buckets.", "No buckets.", "Aucun bucket."),
        ["chart.hist.caption"]         = Lang("Buckets (Anzahl pro Schranke)", "Buckets (count per bound)", "Buckets (nombre par borne)"),
        ["chart.hist.aria"]            = Lang("Histogramm", "Histogram", "Histogramme"),
        ["grafana.logs.more"]          = Lang("… {0} weitere im Zeitraum", "… {0} more in range", "… {0} de plus dans la période"),
        // --- Grafana: Panel-Leer-/Fehlerzustände (GrafanaPanelRenderer) -----
        // de wortwörtlich aus den vormals hard-codierten Literalen übernommen.
        ["grafana.empty.noTargets"]     = Lang("Keine PromQL-Targets im Panel.", "No PromQL targets in the panel.", "Aucune cible PromQL dans le panneau."),
        ["grafana.empty.noData"]        = Lang("Keine Daten im Zeitraum.", "No data in the range.", "Aucune donnée dans la plage."),
        ["grafana.empty.noValue"]       = Lang("Kein Wert im Zeitraum.", "No value in the range.", "Aucune valeur dans la plage."),
        ["grafana.empty.noValues"]      = Lang("Keine Werte im Zeitraum.", "No values in the range.", "Aucune valeur dans la plage."),
        ["grafana.empty.noObserved"]    = Lang("Keine beobachteten Werte im Zeitraum.", "No observed values in the range.", "Aucune valeur observée dans la plage."),
        ["grafana.empty.noTableRows"]   = Lang("Keine Reihen für die Tabelle.", "No rows for the table.", "Aucune ligne pour le tableau."),
        ["grafana.empty.noGauge"]       = Lang("Kein Wert für die Gauge.", "No value for the gauge.", "Aucune valeur pour la jauge."),
        ["grafana.empty.noBarGauge"]    = Lang("Keine Reihen für das Bargauge.", "No rows for the bargauge.", "Aucune ligne pour le bargauge."),
        ["grafana.empty.noPie"]         = Lang("Keine Reihen für das Kreisdiagramm.", "No rows for the pie chart.", "Aucune ligne pour le graphique en secteurs."),
        ["grafana.empty.noHistBuckets"] = Lang("Keine Histogramm-Buckets (le) im Zeitraum.", "No histogram buckets (le) in the range.", "Aucun bucket d'histogramme (le) dans la plage."),
        ["grafana.empty.noDataPoints"]  = Lang("Keine Datenpunkte im Zeitraum.", "No data points in the range.", "Aucun point de donnée dans la plage."),
        ["grafana.empty.noObservedZero"]= Lang("Keine beobachteten Werte im Zeitraum (alle Buckets ≤ 0).", "No observed values in the range (all buckets ≤ 0).", "Aucune valeur observée dans la plage (tous les buckets ≤ 0)."),
        ["grafana.empty.noLogQuery"]    = Lang("Keine Log-Abfrage im Panel.", "No log query in the panel.", "Aucune requête de log dans le panneau."),
        ["grafana.empty.noLogs"]        = Lang("Keine Logs im Zeitraum.", "No logs in the range.", "Aucun log dans la plage."),
        ["grafana.err.logsNoStore"]     = Lang("Log-Panels benötigen Heimdalls Log-Store (IHeimdallQuery) — im eingebetteten Modus ohne Query nicht verfügbar.", "Log panels require Heimdall's log store (IHeimdallQuery) — unavailable in embedded mode without query.", "Les panneaux de logs nécessitent le log store de Heimdall (IHeimdallQuery) — indisponible en mode embarqué sans query."),
        ["grafana.err.unsupportedDatasource"] = Lang("Datenquelle '{0}' wird nicht unterstützt (nur PromQL).", "Datasource '{0}' not supported (PromQL only).", "Source de données « {0} » non supportée (PromQL uniquement)."),
        ["grafana.err.panel"]           = Lang("Panel '{0}': {1}", "Panel '{0}': {1}", "Panneau « {0} » : {1}"),
        ["metrics.raw.caption"]        = Lang("Roh-Tabelle", "Raw table", "Tableau brut"),
        ["metrics.raw.summary"]        = Lang("Roh-Tabelle ({0} Punkt(e))", "Raw table ({0} point(s))", "Tableau brut ({0} point(s))"),
        ["metrics.count.bottom"]       = Lang("{0} Punkt(e) · {1} Metrik-Punkte gesamt", "{0} point(s) · {1} metric points total", "{0} point(s) · {1} points de métrique au total"),
        ["metrics.col.type"]           = Lang("Typ", "Type", "Type"),
        ["metrics.col.value"]          = Lang("Wert", "Value", "Valeur"),
        ["metrics.col.count"]          = Lang("Count", "Count", "Count"),
        ["metrics.col.sum"]            = Lang("Sum", "Sum", "Somme"),
        ["metrics.col.attrs"]          = Lang("Attrs", "Attrs", "Attrs"),

        // --- Trace-Detail ----------------------------------------------------
        ["trace.back"]            = Lang("← zurück zu Traces", "← back to Traces", "← retour aux Traces"),
        ["trace.title.prefix"]    = Lang("Trace ", "Trace ", "Trace "),
        ["trace.empty.spans"]     = Lang("Keine Spans zu dieser Trace-ID.", "No spans for this trace ID.", "Aucune span pour cet ID de trace."),
        ["trace.timeline.heading"]= Lang("Span-Zeitstrahl", "Span timeline", "Chronologie des spans"),
        ["trace.spans.heading"]   = Lang("Spans", "Spans", "Spans"),
        ["trace.col.span"]        = Lang("Span", "Span", "Span"),
        ["trace.col.kind"]        = Lang("Kind", "Kind", "Kind"),
        ["trace.status.err"]      = Lang("Fehler", "Error", "Erreur"),
        ["trace.status.errprefix"]= Lang("✕ ", "✕ ", "✕ "),
        ["trace.attrs.count"]     = Lang("{0} Attribut(e)", "{0} attribute(s)", "{0} attribut(s)"),
        ["trace.attrs.none"]      = Lang("keine Attribute", "no attributes", "aucun attribut"),
        ["trace.events"]          = Lang("Events", "Events", "Événements"),
        ["trace.resource"]        = Lang("Resource", "Resource", "Ressource"),
        ["trace.logs.heading"]    = Lang("Logs dieses Traces", "Logs of this trace", "Logs de cette trace"),
        ["trace.logs.empty"]      = Lang("Keine Logs mit dieser Trace-ID verknüpft.", "No logs linked to this trace ID.", "Aucun log lié à cet ID de trace."),

        // --- Logs-Seite ------------------------------------------------------
        ["logs.filter.search"]    = Lang("Suche", "Search", "Recherche"),
        ["logs.filter.text"]      = Lang("Text", "Text", "Texte"),
        ["logs.filter.severity"]  = Lang("Min. Severity", "Min. severity", "Sév. min."),
        ["logs.filter.submit"]    = Lang("Suchen", "Search", "Rechercher"),
        ["logs.expand"]           = Lang("alle aufklappen", "expand all", "tout déplier"),
        ["logs.syntax.prefix"]    = Lang("Syntax:", "Syntax:", "Syntaxe :"),
        ["logs.syntax.example"]   = Lang("{schlüssel=\"wert\"}", "{key=\"value\"}", "{clé=\"valeur\"}"),
        ["logs.syntax.f1"]        = Lang("Feldfilter (index-gestützt, auch Resource-Attribute wie", "Field filter (index-backed, also resource attributes like", "Filtre de champ (indexé, y compris les attributs de ressource comme"),
        ["logs.syntax.f2"]        = Lang(") ·", ") ·", ") ·"),
        ["logs.syntax.bodytext"]  = Lang("Body-Volltext · mehrere Filter kommasepariert · Operatoren", "Body full-text · multiple filters comma-separated · operators", "Corps plein texte · plusieurs filtres séparés par des virgules · opérateurs"),
        ["logs.empty.more.title"] = Lang("Keine weiteren Logs", "No more logs", "Plus aucun log"),
        ["logs.empty.more.body"]  = Lang("In diesem Bereich gibt es keine älteren Logs mehr. ", "There are no older logs in this range. ", "Il n'y a plus de logs plus anciens dans cette plage. "),
        ["logs.empty.title"]      = Lang("Keine Logs gefunden", "No logs found", "Aucun log trouvé"),
        ["logs.empty.body"]       = Lang("Im gewählten Zeitraum liegen keine Logs vor. Suchtext entfernen, Severity-Filter lockern oder Telemetrie senden.", "No logs in the selected time range. Remove the search text, loosen the severity filter or send telemetry.", "Aucun log dans la plage sélectionnée. Supprimez le texte de recherche, assouplissez le filtre de sévérité ou envoyez de la télémétrie."),
        ["logs.count"]            = Lang("{0} auf dieser Seite · {1} Logs gesamt (ungefiltert)", "{0} on this page · {1} logs total (unfiltered)", "{0} sur cette page · {1} logs au total (non filtrés)"),

        // --- Dashboard / Monitoring -----------------------------------------
        ["dashboard.subtitle"]         = Lang("Monitoring über den Zeitraum „{0}\". Requests={1}, Errors={2}, Latenz={3}.", "Monitoring over the „{0}\" range. Requests={1}, Errors={2}, Latency={3}.", "Surveillance sur la plage « {0} ». Requêtes={1}, Erreurs={2}, Latence={3}."),
        ["dashboard.filter.requests"]  = Lang("Requests", "Requests", "Requêtes"),
        ["dashboard.filter.errors"]    = Lang("Errors", "Errors", "Erreurs"),
        ["dashboard.filter.duration"]  = Lang("Latenz-Metrik", "Latency metric", "Métrique de latence"),
        ["dashboard.discovery.hint.a"] = Lang("Request-Counter wählen zum Belegen des Dashboards (Errors optional, Latenz default ", "Pick a request counter to populate the dashboard (errors optional, latency defaults to ", "Choisissez un compteur de requêtes pour remplir le tableau de bord (erreurs facultatives, latence par défaut "),
        ["dashboard.discovery.hint.b"] = Lang("):", "):", ") :"),
        ["dashboard.kpi.rate"]         = Lang("Rate aktuell", "Current rate", "Taux actuel"),
        ["dashboard.kpi.peak"]         = Lang("Spitzenlast", "Peak load", "Pic de charge"),
        ["dashboard.kpi.errrate"]      = Lang("Errorrate", "Error rate", "Taux d'erreur"),
        ["dashboard.kpi.uptime"]       = Lang("Uptime", "Uptime", "Disponibilité"),
        ["dashboard.kpi.response"]     = Lang("Antwortzeit", "Response time", "Temps de réponse"),
        ["dashboard.kpi.rate.sub"]     = Lang("∅ {0}/s", "avg {0}/s", "∅ {0}/s"),
        ["dashboard.kpi.peak.sub"]     = Lang("max /s", "max /s", "max /s"),
        ["dashboard.kpi.errrate.sub"]  = Lang("gesamt {0}", "total {0}", "total {0}"),
        ["dashboard.kpi.uptime.sub"]   = Lang(" Calls {0}", " Calls {0}", " Appels {0}"),
        ["dashboard.heading.rate"]     = Lang("Rate „{0}\" (/s)", "Rate „{0}\" (/s)", "Taux « {0} » (/s)"),
        ["dashboard.heading.errrate"]  = Lang("Errorrate", "Error rate", "Taux d'erreur"),
        ["dashboard.heading.latency"]  = Lang("Antwortzeiten (p50 / p95 / p99)", "Response times (p50 / p95 / p99)", "Temps de réponse (p50 / p95 / p99)"),
        ["dashboard.latency.empty"]    = Lang("Keine Latenz-Histogramm-Punkte für „{0}\" — p50/p95/p99 nicht verfügbar.", "No latency histogram points for „{0}\" — p50/p95/p99 unavailable.", "Aucun point d'histogramme de latence pour « {0} » — p50/p95/p99 indisponibles."),
        ["dashboard.logs.heading"]     = Lang("Logs im Zeitraum ({0})", "Logs in the range ({0})", "Logs dans la plage ({0})"),
        ["dashboard.traces.heading"]   = Lang("Traces im Zeitraum ({0})", "Traces in the range ({0})", "Traces dans la plage ({0})"),
        ["dashboard.logs.empty"]       = Lang("Keine Logs im Zeitraum.", "No logs in the range.", "Aucun log dans la plage."),
        ["dashboard.traces.empty"]     = Lang("Keine Traces im Zeitraum.", "No traces in the range.", "Aucune trace dans la plage."),
        ["dashboard.nodata.prefix"]    = Lang("Keine Daten für „{0}\" — Counter-Namen prüfen, Zeitraum erweitern oder ", "No data for „{0}\" — check counter names, widen the range or ", "Aucune donnée pour « {0} » — vérifiez les noms de compteurs, élargissez la plage ou "),
        ["dashboard.nodata.link"]      = Lang("verfügbare Metriken", "available metrics", "métriques disponibles"),
        ["dashboard.nodata.suffix"]    = Lang(" anzeigen.", ".", "."),

        // --- Endpoints -------------------------------------------------------
        ["endpoint.subtitle"]          = Lang("API-Übersicht über den Zeitraum „{0}\". Server-Spans={1}.", "API overview over the „{0}\" range. Server spans={1}.", "Vue d'ensemble de l'API sur la plage « {0} ». Spans serveur={1}."),
        ["endpoint.back.all"]          = Lang("← alle Controller", "← all controllers", "← tous les contrôleurs"),
        ["endpoint.controller.label"]  = Lang("Controller „{0}\"", "Controller „{0}\"", "Contrôleur « {0} »"),
        ["endpoint.spans.label"]       = Lang("Server-Spans={0}.", "Server spans={0}.", "Spans serveur={0}."),
        ["endpoint.filter.controllerattr"] = Lang("Controller-Attr", "Controller attr", "Attr. contrôleur"),
        ["endpoint.filter.actionattr"] = Lang("Action-Attr", "Action attr", "Attr. action"),
        ["endpoint.filter.routeattr"]  = Lang("Route-Attr", "Route attr", "Attr. route"),
        ["endpoint.kpi.calls"]         = Lang("Aufrufe", "Calls", "Appels"),
        ["endpoint.kpi.avg.resp"]      = Lang("∅ Antwortzeit", "avg response time", "∅ temps de réponse"),
        ["endpoint.kpi.avg"]           = Lang("avg", "avg", "moy."),
        ["endpoint.kpi.errrate"]       = Lang("Fehlerrate", "Error rate", "Taux d'erreur"),
        ["endpoint.kpi.err.sub"]       = Lang("Fehler {0}", "Errors {0}", "Erreurs {0}"),
        ["endpoint.col.controller"]    = Lang("Controller", "Controller", "Contrôleur"),
        ["endpoint.col.calls"]         = Lang("Aufrufe", "Calls", "Appels"),
        ["endpoint.col.err"]           = Lang("Fehler", "Errors", "Erreurs"),
        ["endpoint.heading.controllers"]   = Lang("Controller ({0})", "Controllers ({0})", "Contrôleurs ({0})"),
        ["endpoint.heading.endpoints"]     = Lang("Endpoints / Actions ({0})", "Endpoints / Actions ({0})", "Endpoints / Actions ({0})"),
        ["endpoint.empty.controllers"] = Lang("Keine Controller im Zeitraum.", "No controllers in the range.", "Aucun contrôleur dans la plage."),
        ["endpoint.empty.endpoints"]   = Lang("Keine Endpoints für Controller „{0}\" im Zeitraum.", "No endpoints for controller „{0}\" in the range.", "Aucun endpoint pour le contrôleur « {0} » dans la plage."),
        ["endpoint.empty.nospans"]     = Lang("Keine Server-Spans im Zeitraum (Limit={0}). Attribut-Namen prüfen oder Zeitraum vergrößern.", "No server spans in the range (limit={0}). Check attribute names or widen the range.", "Aucune span serveur dans la plage (limite={0}). Vérifiez les noms d'attributs ou élargissez la plage."),

        // --- Login -----------------------------------------------------------
        ["login.heading"]        = Lang("Anmelden", "Sign in", "Connexion"),
        ["login.field.user"]     = Lang("Benutzername", "Username", "Nom d'utilisateur"),
        ["login.field.password"] = Lang("Passwort", "Password", "Mot de passe"),
        ["login.submit"]         = Lang("Anmelden", "Sign in", "Se connecter"),
        ["login.hint"]           = Lang("Heimdall-Observability · Session-Cookie mit Timeout", "Heimdall observability · session cookie with timeout", "Heimdall observabilité · cookie de session avec expiration"),

        // --- Alerts: Liste ---------------------------------------------------
        ["alert.subtitle"]         = Lang("Konfigurierbare Alarmregeln über Logs, Metriken und Traces.", "Configurable alert rules over logs, metrics and traces.", "Règles d'alerte configurables sur les logs, métriques et traces."),
        ["alert.disabled.hint"]    = Lang("· Auswertung deaktiviert (Heimdall:Alerting:Enabled=false) — Regeln sind verwaltbar, werden aber nicht getaktet.", "· Evaluation disabled (Heimdall:Alerting:Enabled=false) — rules are manageable but not scheduled.", "· Évaluation désactivée (Heimdall:Alerting:Enabled=false) — les règles sont gérables mais non planifiées."),
        ["alert.rules.count"]      = Lang("· {0} Regel(n).", "· {0} rule(s).", "· {0} règle(s)."),
        ["alert.filter.state"]     = Lang("Zustand", "State", "État"),
        ["alert.filter.update"]    = Lang("Aktualisieren", "Refresh", "Actualiser"),
        ["alert.new.rule"]         = Lang("+ Neue Regel", "+ New rule", "+ Nouvelle règle"),
        ["alert.empty.prefix"]     = Lang("Keine Alarmregeln vorhanden — ", "No alert rules — ", "Aucune règle d'alerte — "),
        ["alert.empty.link"]       = Lang("neue Regel anlegen", "create a new rule", "créer une règle"),
        ["alert.empty.suffix"]     = Lang(".", ".", "."),
        ["alert.kpi.firing.sub"]   = Lang("aktive Alarme", "active alarms", "alarmes actives"),
        ["alert.kpi.pending.sub"]  = Lang("wartet auf for", "waiting for for", "en attente de for"),
        ["alert.kpi.resolved.sub"] = Lang("kürzlich aufgelöst", "recently resolved", "récemment résolue"),
        ["alert.kpi.rules.sub"]    = Lang("gesamt", "total", "total"),
        ["alert.kpi.rules.label"]  = Lang("Regeln", "Rules", "Règles"),
        ["alert.col.name"]         = Lang("Name", "Name", "Nom"),
        ["alert.col.signal"]       = Lang("Signal", "Signal", "Signal"),
        ["alert.col.state"]        = Lang("Zustand", "State", "État"),
        ["alert.col.value"]        = Lang("Wert", "Value", "Valeur"),
        ["alert.col.lasteval"]     = Lang("Letzte Auswertung", "Last evaluation", "Dernière éval."),
        ["alert.col.channels"]     = Lang("Kanäle", "Channels", "Canaux"),
        ["alert.col.active"]       = Lang("aktiv", "active", "actif"),
        ["alert.table.caption"]    = Lang("Alarmregeln", "Alert rules", "Règles d'alerte"),
        ["common.yes"]             = Lang("ja", "yes", "oui"),
        ["common.no"]              = Lang("nein", "no", "non"),
        ["common.dontcare"]        = Lang("egal", "any", "indifférent"),

        // --- Alerts: Detail --------------------------------------------------
        ["alert.detail.notfound"]      = Lang("Regel nicht gefunden", "Rule not found", "Règle introuvable"),
        ["alert.detail.back"]          = Lang("← alle Alerts", "← all alerts", "← toutes les alertes"),
        ["alert.detail.empty"]         = Lang("Keine Regel mit Id „{0}\".", "No rule with id „{0}\".", "Aucune règle avec l'id « {0} »."),
        ["alert.detail.kpi.lastval"]   = Lang("Letzter Wert", "Last value", "Dernière valeur"),
        ["alert.detail.col.logtext"]   = Lang("Log-Text", "Log text", "Texte de log"),
        ["alert.detail.col.minseverity"] = Lang("Min. Severity", "Min. severity", "Sév. min."),
        ["alert.detail.col.errtraces"] = Lang("nur Fehler-Traces", "error traces only", "traces d'erreur uniquement"),
        ["alert.detail.col.window"]    = Lang("Fenster", "Window", "Fenêtre"),
        ["alert.detail.col.threshold"] = Lang("Schwellen", "Threshold", "Seuil"),
        ["alert.detail.col.evalinterval"] = Lang("Eval-Takt", "Eval interval", "Takt d'éval."),
        ["alert.detail.col.description"] = Lang("Beschreibung", "Description", "Description"),
        ["alert.detail.col.since"]     = Lang("seit", "since", "depuis"),
        ["alert.detail.col.lastnotif"] = Lang("Letzte Benachrichtigung", "Last notification", "Dernière notification"),
        ["alert.detail.col.note"]      = Lang("Hinweis", "Note", "Note"),
        ["alert.detail.state.heading"] = Lang("Zustand", "State", "État"),
        ["alert.detail.state.empty"]   = Lang("Noch nicht ausgewertet (Zustand implizit Ok).", "Not yet evaluated (state implicitly Ok).", "Pas encore évaluée (état implicitement Ok)."),
        ["alert.detail.eval.global"]   = Lang("global", "global", "global"),
        ["alert.detail.edit"]          = Lang("Bearbeiten", "Edit", "Modifier"),
        ["alert.detail.delete"]        = Lang("Löschen", "Delete", "Supprimer"),

        // --- Alerts: Editor --------------------------------------------------
        ["alert.editor.heading.new"]   = Lang("Neue Alarm-Regel", "New alert rule", "Nouvelle règle d'alerte"),
        ["alert.editor.heading.edit"]  = Lang("Alarm-Regel bearbeiten", "Edit alert rule", "Modifier la règle d'alerte"),
        ["alert.editor.legend.general"] = Lang("Allgemein", "General", "Général"),
        ["alert.editor.legend.metric"]  = Lang("Metrik (PromQL)", "Metric (PromQL)", "Métrique (PromQL)"),
        ["alert.editor.legend.log"]     = Lang("Log", "Log", "Log"),
        ["alert.editor.legend.trace"]   = Lang("Trace", "Trace", "Trace"),
        ["alert.editor.legend.channels"] = Lang("Kanäle", "Channels", "Canaux"),
        ["alert.editor.legend.window"]  = Lang("Fenster & Schwellen & Verhalten", "Window & thresholds & behavior", "Fenêtre & seuils & comportement"),
        ["alert.editor.field.enabled"]  = Lang("aktiviert", "enabled", "activée"),
        ["alert.editor.signal.metric"]  = Lang("Metrik (PromQL)", "Metric (PromQL)", "Métrique (PromQL)"),
        ["alert.editor.signal.log"]     = Lang("Log (Volltext + Severity)", "Log (full text + severity)", "Log (plein texte + sévérité)"),
        ["alert.editor.signal.trace"]   = Lang("Trace (Fehler/Service/Name)", "Trace (error/service/name)", "Trace (erreur/service/nom)"),
        ["alert.editor.metric.hint"]    = Lang("Feuert, wenn der Ausdruck einen nicht-leeren Vektor liefert (Vergleich behält Treffer). Bsp: ", "Fires when the expression yields a non-empty vector (comparison keeps matches). E.g. ", "Se déclenche quand l'expression renvoie un vecteur non vide (la comparaison conserve les correspondances). Ex. "),
        ["alert.editor.log.hint.a"]     = Lang("Volltext auf ", "Full text on ", "Plein texte sur "),
        ["alert.editor.log.hint.b"]     = Lang(" (FTS5/tsvector, optional) + Mindest-Severity. Feuert bei > Threshold Treffern im Fenster.", " (FTS5/tsvector, optional) + min severity. Fires on > threshold matches in the window.", " (FTS5/tsvector, facultatif) + sévérité min. Se déclenche sur > correspondances que le seuil dans la fenêtre."),
        ["alert.editor.trace.hint"]     = Lang("Feuert bei > Threshold Traces im Fenster (filterbar nach Fehler/Service/Name).", "Fires on > threshold traces in the window (filterable by error/service/name).", "Se déclenche sur > traces que le seuil dans la fenêtre (filtrable par erreur/service/nom)."),
        ["alert.editor.field.window"]   = Lang("Fenster (s)", "Window (s)", "Fenêtre (s)"),
        ["alert.editor.field.for"]      = Lang("for (s)", "for (s)", "for (s)"),
        ["alert.editor.field.evalinterval"] = Lang("Eval-Takt (s)", "Eval interval (s)", "Takt d'éval. (s)"),
        ["alert.editor.placeholder.for"]   = Lang("0 = sofort feuern", "0 = fire immediately", "0 = déclencher immédiatement"),
        ["alert.editor.placeholder.eval"]  = Lang("0 = globaler Takt", "0 = global interval", "0 = takt global"),
        ["alert.editor.placeholder.logtext"] = Lang("z. B. timeout (leer = alle)", "e.g. timeout (empty = all)", "ex. timeout (vide = toutes)"),
        ["alert.editor.channel.disabled"] = Lang("(deaktiviert)", "(disabled)", "(désactivé)"),
        ["alert.editor.save"]          = Lang("Speichern", "Save", "Enregistrer"),
        ["alert.editor.cancel"]        = Lang("Abbrechen", "Cancel", "Annuler"),

        // --- Grafana: Liste --------------------------------------------------
        ["grafana.list.subtitle"]    = Lang("Importierte Dashboards werden lokal gespeichert und in Heimdall selbst gerendert — keine externe Grafana-Instanz nötig.", "Imported dashboards are stored locally and rendered in Heimdall itself — no external Grafana instance needed.", "Les tableaux de bord importés sont stockés localement et rendus dans Heimdall même — aucune instance Grafana externe nécessaire."),
        ["grafana.list.import"]      = Lang("+ Dashboard importieren", "+ Import dashboard", "+ Importer un tableau de bord"),
        ["grafana.list.empty"]       = Lang("Noch keine Dashboards importiert. Ein Grafana-Dashboard-JSON (mit PromQL-Panels) hochladen → es wird gegen die Heimdall-Metriken gerendert.", "No dashboards imported yet. Upload a Grafana dashboard JSON (with PromQL panels) → it will be rendered against the Heimdall metrics.", "Aucun tableau de bord importé pour l'instant. Téléversez un JSON de tableau de bord Grafana (avec des panneaux PromQL) → il sera rendu sur les métriques Heimdall."),
        ["grafana.list.col.title"]   = Lang("Titel", "Title", "Titre"),
        ["grafana.list.col.panels"]  = Lang("Panels", "Panels", "Panneaux"),

        // --- Grafana: Import -------------------------------------------------
        ["grafana.import.subtitle"]  = Lang("Ein exportiertes Grafana-Dashboard-JSON (mit Prometheus/PromQL-Panels) hochladen. Heimdall wertet die Panel-Ausdrücke über die eigene PromQL-Engine aus und rendert sie server-seitig.", "Upload an exported Grafana dashboard JSON (with Prometheus/PromQL panels). Heimdall evaluates the panel expressions with its own PromQL engine and renders them server-side.", "Téléversez un JSON de tableau de bord Grafana exporté (avec des panneaux Prometheus/PromQL). Heimdall évalue les expressions des panneaux avec son propre moteur PromQL et les rend côté serveur."),
        ["grafana.import.file"]      = Lang("Datei", "File", "Fichier"),
        ["grafana.import.json"]      = Lang("… oder JSON direkt einfügen", "… or paste JSON directly", "… ou coller le JSON directement"),
        ["grafana.import.submit"]    = Lang("Importieren", "Import", "Importer"),

        // --- Grafana: View ---------------------------------------------------
        ["grafana.view.back"]        = Lang("← Alle Dashboards", "← All dashboards", "← Tous les tableaux de bord"),
        ["grafana.view.panels"]      = Lang("{0} Panels", "{0} Panels", "{0} panneaux"),
        ["grafana.view.notfound"]    = Lang("Dashboard „{0}\" nicht gefunden. ", "Dashboard „{0}\" not found. ", "Tableau de bord « {0} » introuvable. "),
        ["grafana.view.notfound.link"] = Lang("Zur Übersicht", "Back to overview", "Retour à la vue d'ensemble"),
        ["grafana.view.invalid"]     = Lang("Dashboard „{0}\" konnte nicht gelesen werden (ungültiges JSON).", "Dashboard „{0}\" could not be read (invalid JSON).", "Le tableau de bord « {0} » n'a pas pu être lu (JSON invalide)."),
        ["grafana.view.all"]         = Lang("All", "All", "Toutes"),
        ["grafana.view.apply"]       = Lang("Anwenden", "Apply", "Appliquer"),
        // Lazy Panel Loading: Shell-Platzhalter + No-JS-Link + Fehler-Zustand.
        ["grafana.view.panel.loading"] = Lang("Panel wird geladen …", "Loading panel …", "Chargement du panneau …"),
        ["grafana.view.panel.show"]    = Lang("Panel anzeigen", "Show panel", "Afficher le panneau"),
        ["grafana.view.panel.failed"]  = Lang("Panel konnte nicht geladen werden.", "Panel could not be loaded.", "Le panneau n'a pas pu être chargé."),
        // Zurück-Link zum zuvor angesehenen Dashboard (Referer-basiert).
        ["grafana.view.back.prev"]     = Lang("← Zurück", "← Back", "← Retour"),
    };

    /// <summary>
    /// Liefert die Übersetzung für <paramref name="key"/> in <paramref name="lang"/>.
    /// Fallback: lang -> de -> key (wirft nie, liefert nie null).
    /// </summary>
    public static string T(string? lang, string key)
    {
        if (key is null) return string.Empty;
        if (_table.TryGetValue(key, out var byLang))
        {
            if (!string.IsNullOrEmpty(lang) && byLang.TryGetValue(lang, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (byLang.TryGetValue(DefaultLang, out var de) && !string.IsNullOrEmpty(de)) return de;
        }
        return key;
    }

    /// <summary>
    /// Übersetzung mit <see cref="string.Format"/>-Platzhaltern — die HttpContext-
    /// freie Variante für Verbraucher ohne scoped Service (z. B. der statische
    /// <c>GrafanaPanelRenderer</c>, der nur eine Sprache als String mitbekommt).
    /// Fallback wie <see cref="T(string?,string)"/>: lang -> de -> key.
    /// </summary>
    public static string T(string? lang, string key, params object?[] args)
        => args is { Length: > 0 } ? string.Format(T(lang, key), args) : T(lang, key);

    /// <summary>True, wenn <paramref name="lang"/> eine unterstützte Sprache ist.</summary>
    public static bool IsSupported(string? lang) => lang is not null && Languages.Contains(lang);

    private static Dictionary<string, string> Lang(string de, string en, string fr) =>
        new(3, StringComparer.Ordinal) { ["de"] = de, ["en"] = en, ["fr"] = fr };
}

/// <summary>
/// Per-Request-i18n für die Blazor-UI. Liefert die aus dem Request resolvede
/// Sprache (<see cref="Lang"/>) und delegiert an die statische
/// <see cref="HeimdallI18n"/>-Tabelle. Components injizieren via
/// <c>@inject Heimdall.Blazor.IHeimdallI18n I18n</c>.
/// </summary>
public interface IHeimdallI18n
{
    /// <summary>Aktive Sprache für diesen Request (de/en/fr).</summary>
    string Lang { get; }

    /// <summary>Übersetzung für <paramref name="key"/> in der Request-Sprache.</summary>
    string T(string key);

    /// <summary>Übersetzung mit <see cref="string.Format"/>-Platzhaltern.</summary>
    string T(string key, params object?[] args);
}

/// <summary>
/// Scoped Implementierung: resolved die Sprache einmal pro Request aus
/// <c>IHttpContextAccessor</c> (?lang=-Query → heimdall-lang-Cookie →
/// Accept-Language → Default) und delegiert an <see cref="HeimdallI18n"/>.
/// </summary>
internal sealed class HeimdallI18nService : IHeimdallI18n
{
    private readonly string _lang;

    public HeimdallI18nService(IHttpContextAccessor accessor)
        => _lang = Resolve(accessor?.HttpContext);

    public string Lang => _lang;

    public string T(string key) => HeimdallI18n.T(_lang, key);

    public string T(string key, params object?[] args)
        => args is { Length: > 0 }
            ? string.Format(HeimdallI18n.T(_lang, key), args)
            : HeimdallI18n.T(_lang, key);

    private static string Resolve(HttpContext? ctx)
    {
        if (ctx is not null)
        {
            // Read-only Preview/Teilen via ?lang=xx (setzt KEINEN Cookie).
            var q = ctx.Request.Query["lang"].ToString();
            if (HeimdallI18n.IsSupported(q)) return q!;

            // Persistente Wahl via Cookie.
            if (ctx.Request.Cookies.TryGetValue("heimdall-lang", out var cookie)
                && HeimdallI18n.IsSupported(cookie))
                return cookie;

            // Best-Effort: Accept-Language-Header.
            var al = ctx.Request.Headers.AcceptLanguage.ToString();
            if (!string.IsNullOrEmpty(al))
            {
                foreach (var part in al.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var raw = part.AsSpan();
                    var semi = raw.IndexOf(';');
                    if (semi >= 0) raw = raw.Slice(0, semi);
                    var trimmed = raw.Trim();
                    if (trimmed.Length >= 2)
                    {
                        var code = trimmed.Slice(0, 2).ToString();
                        if (HeimdallI18n.IsSupported(code)) return code;
                    }
                }
            }
        }
        return HeimdallI18n.DefaultLang;
    }
}