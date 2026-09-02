using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Heimdall.Blazor.Grafana;

// ---------------------------------------------------------------------------
// Dashboard-Editor-Backend. Anders als der (bewusst verlustbehaftete) Parser
// in GrafanaDashboardModel arbeitet der Editor AUSSCHLIESSLICH auf dem rohen
// JSON: Mutationen via System.Text.Json.Nodes touchieren nur die Knoten, die
// Formularfelder abbilden — alles uebrige (datasource.uid, fieldConfig.
// overrides, options.*, schemaVersion, unbekannte Felder) bleibt unberuehrt.
// Ein editiertes importiertes Dashboard verliert daher nichts, was der
// Parser selbst nicht liest (kein Model→JSON-Roundtrip, den es nicht gibt).
//
// Panel-Identitaet ist ein Pfad-Key in das panels-Array (nicht GrafanaPanel.
// Id — nicht eindeutig — und nicht der Slot-Index aus ExpandPanels — Repeat-
// Expansion): "3" -> root.panels[3], "1.3" -> panels[1] (Row) -> .panels[3].
// Bei rows[]-Dashboards (legacy) addressiert "i.j" rows[i].panels[j].
// ListPanels liefert dieselben Keys, die UpsertPanel/DeletePanel aufloesen.
// ---------------------------------------------------------------------------

/// <summary>
/// Verlustfreie Bearbeitung von Dashboard-JSON (roh, <see cref="JsonNode"/>-basiert).
/// Wirft <see cref="ArgumentException"/> mit deutscher Meldung bei ungueltigen
/// Eingaben (gleiche Konvention wie <see cref="FileGrafanaDashboardStore.Save"/>).
/// </summary>
public static class GrafanaDashboardEditor
{
    // --- Listen / Form-DTOs -------------------------------------------------

    /// <summary>Panel-Eintrag der Editor-Hub-Liste (Key = Pfad-Key ins rohe JSON).</summary>
    public sealed record PanelEntry(string Key, int? Id, string Title, string Type,
                                    GrafanaGridPos GridPos, int TargetCount);

    /// <summary>Template-Variablen-Eintrag der Editor-Hub-Liste.</summary>
    public sealed record VarEntry(string Key, string Name, string Type, string Query);

    /// <summary>Formular-Daten eines Panels (Seed via <see cref="ReadPanel"/>).</summary>
    public sealed record PanelForm(
        string Title, string Type,
        int X, int Y, int W, int H,
        IReadOnlyList<TargetForm> Targets,
        string? Unit,
        IReadOnlyList<ThresholdForm> Thresholds,
        string? Repeat,
        string? GraphMode,
        bool IsRow = false);

    /// <summary>Ein PromQL-Target im Formular (leere Expr wird beim Speichern uebersprungen).</summary>
    public sealed record TargetForm(string Expr, string? LegendFormat, bool Instant);

    /// <summary>Ein Threshold-Schritt (null-Wert = Basis-Schritt, wie in Grafana).</summary>
    public sealed record ThresholdForm(double? Value, string Color);

    /// <summary>Formular-Daten einer Template-Variablen.</summary>
    public sealed record VariableForm(string Name, string Type, string Query,
                                      string? CurrentValue, bool IncludeAll, bool Multi);

    // --- Lesen (Seed der Formulare) -----------------------------------------

    /// <summary>Alle Panels (inkl. Row-Header) mit Pfad-Key; Reihenfolge wie im JSON.</summary>
    public static IReadOnlyList<PanelEntry> ListPanels(string rawJson)
    {
        var list = new List<PanelEntry>();
        var root = ParseRoot(rawJson);
        if (root is null) return list;
        foreach (var (panel, key) in ResolvePanels(root))
            list.Add(new PanelEntry(key, NullableInt(panel, "id"), S(panel, "title") ?? string.Empty,
                S(panel, "type") ?? string.Empty, ReadGrid(panel), TargetCount(panel)));
        return list;
    }

    /// <summary>Alle Template-Variablen mit Key ("v&lt;index&gt;").</summary>
    public static IReadOnlyList<VarEntry> ListVariables(string rawJson)
    {
        var list = new List<VarEntry>();
        var root = ParseRoot(rawJson);
        if (root is null) return list;
        var vars = VarListNode(root);
        if (vars is null) return list;
        for (int i = 0; i < vars.Count; i++)
        {
            if (vars[i] is not JsonObject v) continue;
            list.Add(new VarEntry("v" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                S(v, "name") ?? string.Empty, S(v, "type") ?? string.Empty, S(v, "query") ?? string.Empty));
        }
        return list;
    }

    /// <summary>
    /// Ordnet gerenderte Panels (Reihenfolge wie <c>GrafanaDashboardRender.ExpandPanels</c>)
    /// ihren Pfad-Keys im rohen JSON zu — Grundlage für die Edit-Links direkt am
    /// Panel in der Dashboard-Ansicht. Match per Titel/Typ/GridPos (aus derselben
    /// Quelle, daher exakt); Repeat-Kopien desselben Panels teilen den Key.
    /// null = kein Treffer (Panel vom Parser verworfen) → kein Edit-Link.
    /// </summary>
    public static IReadOnlyList<string?> MatchRenderKeys(string rawJson, IReadOnlyList<GrafanaPanel> panels)
    {
        var result = new string?[panels.Count];
        // Roh-Panels ohne Row-Header (Rows sind Layout, tauchen im Render nicht auf);
        // Konsum-Flag gegen Doppel-Zuordnung inhaltsgleicher Panels.
        var raw = new List<(PanelEntry Entry, bool Consumed)>();
        foreach (var e in ListPanels(rawJson))
            if (!string.Equals(e.Type, "row", StringComparison.OrdinalIgnoreCase))
                raw.Add((e, false));
        var matched = new Dictionary<GrafanaPanel, string?>();
        for (int i = 0; i < panels.Count; i++)
        {
            var p = panels[i];
            if (p is null) continue;
            if (matched.TryGetValue(p, out var known)) { result[i] = known; continue; }
            string? key = null;
            for (int j = 0; j < raw.Count; j++)
            {
                if (raw[j].Consumed) continue;
                var e = raw[j].Entry;
                if (string.Equals(e.Title, p.Title, StringComparison.Ordinal)
                    && string.Equals(e.Type, p.Type, StringComparison.Ordinal)
                    && e.GridPos.X == p.GridPos.X && e.GridPos.Y == p.GridPos.Y
                    && e.GridPos.W == p.GridPos.W && e.GridPos.H == p.GridPos.H)
                {
                    key = e.Key;
                    raw[j] = (e, true);
                    break;
                }
            }
            matched[p] = key;
            result[i] = key;
        }
        return result;
    }

    /// <summary>
    /// Belegungsvorschlag für ein neues Panel: erstes nicht überlappendes
    /// Platzierungsfenster (w×h), oberste Zeile zuerst, dann links — Grafana-
    /// typische „Auto-Position". Kandidaten-Y sind die Panel-Kanten (0 inklusive)
    /// plus die Grid-Unterkante; Kandidaten-X 0..24−w.
    /// </summary>
    public static (int X, int Y) SuggestNewPos(IReadOnlyList<PanelEntry> panels, int w, int h)
    {
        if (w < 1) w = 1;
        if (w > 24) w = 24;
        if (h < 1) h = 1;

        bool Fits(int x, int y)
        {
            foreach (var p in panels)
            {
                var g = p.GridPos;
                if (x < g.X + g.W && g.X < x + w && y < g.Y + g.H && g.Y < y + h) return false;
            }
            return true;
        }

        var ys = new SortedSet<int>();
        foreach (var p in panels)
        {
            ys.Add(p.GridPos.Y);
            ys.Add(p.GridPos.Y + p.GridPos.H);
        }
        ys.Add(0);
        foreach (var y in ys)
            for (int x = 0; x <= 24 - w; x++)
                if (Fits(x, y)) return (x, y);
        // Kein Slot im Raster: unter alles (Absicherung; praktisch unerreichbar).
        int maxBottom = 0;
        foreach (var p in panels)
            if (p.GridPos.Y + p.GridPos.H > maxBottom) maxBottom = p.GridPos.Y + p.GridPos.H;
        return (0, maxBottom);
    }

    /// <summary>Liest ein Panel in das Formular-DTO; null bei unbekanntem Key.</summary>
    public static PanelForm? ReadPanel(string rawJson, string panelKey)
    {
        var root = ParseRoot(rawJson);
        if (root is null) return null;
        if (!TryResolvePanel(root, panelKey, out var panel)) return null;
        string type = S(panel, "type") ?? string.Empty;
        bool isRow = string.Equals(type, "row", StringComparison.OrdinalIgnoreCase);

        var targets = new List<TargetForm>();
        if (panel["targets"] is JsonArray ts)
            foreach (var t in ts)
            {
                if (t is not JsonObject to) continue;
                string expr = S(to, "expr") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(expr)) continue;   // wie der Parser: droppt leere expr
                targets.Add(new TargetForm(expr, S(to, "legendFormat"), Bool(to, "instant")));
            }

        var thresholds = new List<ThresholdForm>();
        if (panel["fieldConfig"]?["defaults"]?["thresholds"]?["steps"] is JsonArray steps)
            foreach (var s in steps)
            {
                if (s is not JsonObject so) continue;
                thresholds.Add(new ThresholdForm(NullableDouble(so, "value"), S(so, "color") ?? "green"));
            }

        return new PanelForm(
            S(panel, "title") ?? string.Empty, type,
            ReadGrid(panel).X, ReadGrid(panel).Y, ReadGrid(panel).W, ReadGrid(panel).H,
            targets,
            S(panel["fieldConfig"]?["defaults"], "unit"),
            thresholds,
            S(panel, "repeat"),
            S(panel["options"], "graphMode"),
            isRow);
    }

    /// <summary>Liest eine Template-Variable in das Formular-DTO; null bei unbekanntem Key.</summary>
    public static VariableForm? ReadVariable(string rawJson, string varKey)
    {
        var root = ParseRoot(rawJson);
        if (root is null) return null;
        if (!TryResolveVariable(root, varKey, out var v)) return null;
        return new VariableForm(S(v, "name") ?? string.Empty, S(v, "type") ?? "query",
            S(v, "query") ?? string.Empty, S(v["current"], "value"),
            Bool(v, "includeAll"), Bool(v, "multi"));
    }

    // --- Dashboard-Ebene -----------------------------------------------------

    /// <summary>Skeleton fuer ein neues Dashboard (parsebar durch GrafanaDashboardModel.Parse).</summary>
    public static string CreateNew(string title, string? uid = null)
    {
        var root = new JsonObject
        {
            ["uid"] = string.IsNullOrWhiteSpace(uid) ? NewUid() : uid,
            ["title"] = title ?? string.Empty,
            ["panels"] = new JsonArray(),
            ["templating"] = new JsonObject { ["list"] = new JsonArray() },
            ["schemaVersion"] = 39,
            ["version"] = 1,
            ["time"] = new JsonObject { ["from"] = "now-6h", ["to"] = "now" },
        };
        return root.ToJsonString(Pretty);
    }

    /// <summary>Kopiert ein Dashboard unter neuer Uid (auch wenn die Quelle uid-los ist).</summary>
    public static string Duplicate(string rawJson, string newUid, string? newTitle = null)
    {
        var root = RequireRoot(rawJson);
        root["uid"] = newUid;
        string title = S(root, "title") ?? string.Empty;
        root["title"] = newTitle ?? (title + " (Kopie)");
        return root.ToJsonString(Pretty);
    }

    /// <summary>Setzt den Dashboard-Titel; Uid (und damit die Datei) bleibt unberuehrt.</summary>
    public static string SetTitle(string rawJson, string title)
    {
        var root = RequireRoot(rawJson);
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Dashboard-Titel fehlt", nameof(title));
        root["title"] = title;
        return root.ToJsonString(Pretty);
    }

    // --- Panel-Ebene -----------------------------------------------------------

    /// <summary>
    /// Legt ein Panel neu an (panelKey leer) oder aktualisiert es (Key aufgeloest).
    /// Bei Update werden NUR die Formular-Felder geschrieben — alle uebrigen
    /// Eigenschaften des Panel-Objekts (datasource, overrides, transformations,
    /// options jenseits graphMode) bleiben unberuehrt; Targets erben per Index.
    /// </summary>
    public static string UpsertPanel(string rawJson, string? panelKey, PanelForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var root = RequireRoot(rawJson);
        string title = form.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
            throw new ArgumentException("Panel-Titel fehlt", nameof(form));
        string type = form.Type?.Trim() ?? "timeseries";
        bool isRow = string.Equals(type, "row", System.StringComparison.OrdinalIgnoreCase);

        // Targets validieren (Row-Panels brauchen keine).
        var targets = new List<TargetForm>();
        if (!isRow)
        {
            foreach (var t in form.Targets ?? Array.Empty<TargetForm>())
                if (t is not null && !string.IsNullOrWhiteSpace(t.Expr)) targets.Add(t);
            if (targets.Count == 0)
                throw new ArgumentException("Panel braucht mindestens ein Target mit PromQL-Ausdruck", nameof(form));
        }

        JsonObject panel;
        bool isNew = string.IsNullOrWhiteSpace(panelKey);
        if (isNew)
        {
            panel = new JsonObject();
            panel["id"] = NextPanelId(root);
            var panels = EnsurePanelsArray(root);
            panels.Add(panel);
        }
        else
        {
            if (!TryResolvePanel(root, panelKey!, out panel!))
                throw new ArgumentException("Panel-Key unbekannt: " + panelKey, nameof(panelKey));
        }

        // Titel + Typ (Row-Panels: nur diese beiden + gridPos).
        panel["title"] = title;
        panel["type"] = type;

        // gridPos schreiben (w auf die 24er-Grids clampen, h mind. 1 — Render-Contract).
        // Y wird beim Neu-Anlage NICHT mehr zwangsweise auf die Grid-Unterkante
        // gesetzt: das Formular wird vom Server mit einer kollisionsfreien
        // Suggest-Position vorbelegt (SuggestNewPos); manuelle Änderungen gelten,
        // der Overlap-Check im Save-Endpoint blockt Kollisionen mit klarer Meldung.
        var grid = EnsureObject(panel, "gridPos");
        grid["x"] = Math.Max(0, form.X);
        grid["y"] = Math.Max(0, form.Y);
        grid["w"] = Math.Clamp(form.W, 1, 24);
        grid["h"] = Math.Max(1, form.H);

        if (isRow)
        {
            panel.Remove("targets");
            panel.Remove("repeat");
            return root.ToJsonString(Pretty);
        }

        // targets[]: bestehende Target-Objekte per Index ERBEN (expr/legendFormat/
        // instant überschreiben, refId/datasource/format/interval bleiben), neue als
        // frisches Objekt anhaengen. Blanke Form-Exprs sind oben gefiltert.
        var existing = panel["targets"] as JsonArray;
        panel.Remove("targets");
        var arr = new JsonArray();
        string[] refIds = { "A", "B", "C", "D", "E", "F", "G", "H" };
        for (int i = 0; i < targets.Count; i++)
        {
            var src = (existing is not null && i < existing.Count && existing[i] is JsonObject o) ? o : null;
            // Vom Alt-Array abkoppeln, bevor es ins Neue umhaengt (ueber die IList-
            // Schnittstelle — JsonObject.Remove(string) verdeckt JsonNode.Remove(),
            // das ohnehin erst ab net10 existiert; der Code ist Multi-Target).
            if (src is not null) ((System.Collections.Generic.IList<JsonNode?>)existing!).Remove(src);
            var t = src ?? new JsonObject();
            t["expr"] = targets[i].Expr;
            if (string.IsNullOrWhiteSpace(targets[i].LegendFormat)) t.Remove("legendFormat");
            else t["legendFormat"] = targets[i].LegendFormat;
            if (targets[i].Instant) t["instant"] = true;
            else t.Remove("instant");
            if (S(t, "refId") is null)
                t["refId"] = i < refIds.Length ? refIds[i] : "R" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            arr.Add(t);
        }
        panel["targets"] = arr;

        // repeat
        if (string.IsNullOrWhiteSpace(form.Repeat)) panel.Remove("repeat");
        else panel["repeat"] = form.Repeat;

        // options.graphMode (nur stat-relevant; "none"/leer = Property weg).
        if (string.Equals(type, "stat", System.StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(form.GraphMode)) RemoveGraphMode(panel);
            else EnsureObject(panel, "options")["graphMode"] = form.GraphMode;
        }
        else RemoveGraphMode(panel);

        // fieldConfig.defaults.unit + thresholds.steps (Knoten nur anlegen, wenn gebraucht).
        bool hasUnit = !string.IsNullOrWhiteSpace(form.Unit);
        bool hasThr = form.Thresholds is not null && form.Thresholds.Count > 0;
        if (hasUnit || hasThr)
        {
            var defaults = EnsureObject(EnsureObject(panel, "fieldConfig"), "defaults");
            if (hasUnit) defaults["unit"] = form.Unit;
            else defaults.Remove("unit");
            if (hasThr)
            {
                var th = EnsureObject(defaults, "thresholds");
                var mode = S(th, "mode");   // bestehenden mode erhalten
                var steps = new JsonArray();
                foreach (var s in form.Thresholds!)
                {
                    if (string.IsNullOrWhiteSpace(s.Color)) continue;   // leere Zeile ignorieren
                    steps.Add(new JsonObject { ["color"] = s.Color, ["value"] = s.Value });
                }
                th["steps"] = steps;
                th["mode"] = string.IsNullOrWhiteSpace(mode) ? "absolute" : mode;
            }
            else defaults.Remove("thresholds");
        }
        else if (panel["fieldConfig"] is JsonObject fc && fc["defaults"] is JsonObject d)
        {
            // Beides geleert: Felder entfernen, leere Hüllen mitraeumen.
            d.Remove("unit");
            d.Remove("thresholds");
            if (d.Count == 0) fc.Remove("defaults");
            if (fc.Count == 0) panel.Remove("fieldConfig");
        }

        return root.ToJsonString(Pretty);
    }

    /// <summary>Entfernt das Panel am Key (Row-Panels inkl. ihrer Kind-Panels).</summary>
    public static string DeletePanel(string rawJson, string panelKey)
    {
        var root = RequireRoot(rawJson);
        var (list, index) = ResolveParentList(root, panelKey)
            ?? throw new ArgumentException("Panel-Key unbekannt: " + panelKey, nameof(panelKey));
        if (index < 0 || index >= list.Count)
            throw new ArgumentException("Panel-Key unbekannt: " + panelKey, nameof(panelKey));
        list.RemoveAt(index);
        return root.ToJsonString(Pretty);
    }

    // --- Variablen-Ebene -------------------------------------------------------

    /// <summary>
    /// Legt eine Template-Variable neu an oder aktualisiert sie. Geschrieben wird
    /// genau die Render-Contract-Form (query als Plain-String, current als Objekt
    /// mit text+value — die View-Shell liest current.value).
    /// </summary>
    public static string UpsertVariable(string rawJson, string? varKey, VariableForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var root = RequireRoot(rawJson);
        string name = form.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new ArgumentException("Variablen-Name fehlt", nameof(form));
        string type = (form.Type ?? "query").Trim().ToLowerInvariant() switch
        {
            "custom" => "custom",
            "datasource" => "datasource",
            _ => "query",
        };

        JsonObject v;
        if (string.IsNullOrWhiteSpace(varKey))
        {
            v = new JsonObject();
            var list = EnsureObject(root, "templating").EnsureArray("list");
            list.Add(v);
        }
        else
        {
            if (!TryResolveVariable(root, varKey, out v!))
                throw new ArgumentException("Variablen-Key unbekannt: " + varKey, nameof(varKey));
        }
        v["name"] = name;
        v["type"] = type;
        if (string.IsNullOrWhiteSpace(form.Query)) v.Remove("query");
        else v["query"] = form.Query;
        v["current"] = new JsonObject { ["text"] = form.CurrentValue ?? string.Empty, ["value"] = form.CurrentValue ?? string.Empty };
        v["includeAll"] = form.IncludeAll;
        v["multi"] = form.Multi;
        return root.ToJsonString(Pretty);
    }

    /// <summary>Entfernt die Template-Variable am Key.</summary>
    public static string DeleteVariable(string rawJson, string varKey)
    {
        var root = RequireRoot(rawJson);
        if (!TryResolveVariable(root, varKey, out _))
            throw new ArgumentException("Variablen-Key unbekannt: " + varKey, nameof(varKey));
        int idx = int.Parse(varKey![1..], System.Globalization.CultureInfo.InvariantCulture);
        VarListNode(root)!.RemoveAt(idx);
        return root.ToJsonString(Pretty);
    }

    // --- Rohes JSON (JSON-Editiermodus) ----------------------------------------

    /// <summary>
    /// Ersetzt das Dashboard-JSON vollstaendig. Lenient geparst (Trailing-Commas/
    /// Kommentare uebersprungen); die Uid der Routen wird ERZWUNGEN (eine andere
    /// Uid im JSON wuerde sonst als neue Datei speichern und die Alt-Datei verwaisten).
    /// </summary>
    public static string ReplaceJson(string newJson, string uid)
    {
        var root = ParseRoot(newJson) ?? throw new ArgumentException("Ungültiges Dashboard-JSON", nameof(newJson));
        root["uid"] = uid;
        return root.ToJsonString(Pretty);
    }

    /// <summary>null = ok, sonst deutsche Fehlermeldung (fuer ?err=-Redirect).</summary>
    public static string? Validate(string rawJson)
    {
        try
        {
            var root = ParseRoot(rawJson);
            if (root is null) return "Ungültiges Dashboard-JSON";
            if (GrafanaDashboardModel.Parse(root.ToJsonString()) is null)
                return "Ungültiges Dashboard-JSON";
            return null;
        }
        catch (ArgumentException ex) { return ex.Message; }
        catch { return "Ungültiges Dashboard-JSON"; }
    }

    /// <summary>Neue kollisionsarme Uid (Prefix "d" + 8 Base36-Zeichen — SafeName-konform).</summary>
    public static string NewUid()
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<byte> bytes = stackalloc byte[5];   // 5 Bytes reichen fuer 8 Base36-Zeichen locker
        RandomNumberGenerator.Fill(bytes);
        ulong n = 0;
        foreach (var b in bytes) n = (n << 8) | b;
        Span<char> chars = stackalloc char[8];
        for (int i = chars.Length - 1; i >= 0; i--)
        {
            chars[i] = alphabet[(int)(n % 36)];
            n /= 36;
        }
        return "d" + new string(chars);
    }

    // --- Interna ---------------------------------------------------------------

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static JsonObject RequireRoot(string rawJson)
    {
        var root = ParseRoot(rawJson)
            ?? throw new ArgumentException("Ungültiges Dashboard-JSON", nameof(rawJson));
        return root;
    }

    /// <summary>Parst lenient (Trailing-Commas/Kommentare uebersprungen); null bei Kaputt.</summary>
    private static JsonObject? ParseRoot(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            var options = new JsonNodeOptions { PropertyNameCaseInsensitive = false };
            var root = JsonNode.Parse(rawJson, options,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return root as JsonObject;
        }
        catch { return null; }
    }

    /// <summary>
    /// Alle Panel-Objekte mit Pfad-Key: root.panels[i] (inkl. Row-Header) plus
    /// deren Kind-Panels "i.j"; ohne root.panels stattdessen rows[i].panels[j].
    /// </summary>
    private static List<(JsonObject Panel, string Key)> ResolvePanels(JsonObject root)
    {
        var result = new List<(JsonObject, string)>();
        if (root["panels"] is JsonArray panels)
        {
            for (int i = 0; i < panels.Count; i++)
            {
                if (panels[i] is not JsonObject p) continue;
                string key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.Add((p, key));
                if (IsRow(p) && p["panels"] is JsonArray nested)
                    for (int j = 0; j < nested.Count; j++)
                        if (nested[j] is JsonObject np)
                            result.Add((np, key + "." + j.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        else if (root["rows"] is JsonArray rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] is not JsonObject r || r["panels"] is not JsonArray rp) continue;
                string rowKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (int j = 0; j < rp.Count; j++)
                    if (rp[j] is JsonObject p)
                        result.Add((p, rowKey + "." + j.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return result;
    }

    /// <summary>Loest einen Pfad-Key ("3" bzw. "1.3") zum Panel-Objekt auf.</summary>
    private static bool TryResolvePanel(JsonObject root, string? panelKey, out JsonObject panel)
    {
        panel = null!;
        if (string.IsNullOrWhiteSpace(panelKey)) return false;
        var segments = panelKey!.Split('.');
        if (segments.Length is < 1 or > 2) return false;
        if (!TryParseIndex(segments[0], out var i)) return false;
        if (segments.Length == 1)
        {
            if (root["panels"] is JsonArray panels && i < panels.Count && panels[i] is JsonObject p) { panel = p; return true; }
            if (root["rows"] is JsonArray rows && i < rows.Count && rows[i] is JsonObject r) { panel = r; return true; }
            return false;
        }
        if (!TryParseIndex(segments[1], out var j)) return false;
        // Erst panels[i].panels[j] (Row mit Kind-Panels), dann rows[i].panels[j] (legacy).
        if (root["panels"] is JsonArray pa && i < pa.Count && pa[i] is JsonObject rp1 && rp1["panels"] is JsonArray np1
            && j < np1.Count && np1[j] is JsonObject c1) { panel = c1; return true; }
        if (root["rows"] is JsonArray ra && i < ra.Count && ra[i] is JsonObject rp2 && rp2["panels"] is JsonArray np2
            && j < np2.Count && np2[j] is JsonObject c2) { panel = c2; return true; }
        return false;
    }

    /// <summary>(Eltern-Array, Index) eines Panel-Keys — fuer RemoveAt in DeletePanel.</summary>
    private static (JsonArray List, int Index)? ResolveParentList(JsonObject root, string? panelKey)
    {
        if (string.IsNullOrWhiteSpace(panelKey)) return null;
        var segments = panelKey!.Split('.');
        if (segments.Length is < 1 or > 2) return null;
        if (!TryParseIndex(segments[0], out var i)) return null;
        if (segments.Length == 1)
        {
            if (root["panels"] is JsonArray p1 && i < p1.Count) return (p1, i);
            if (root["rows"] is JsonArray r1 && i < r1.Count) return (r1, i);
            return null;
        }
        if (!TryParseIndex(segments[1], out var j)) return null;
        if (root["panels"] is JsonArray pa && i < pa.Count && pa[i] is JsonObject rp1 && rp1["panels"] is JsonArray np1
            && j < np1.Count) return (np1, j);
        if (root["rows"] is JsonArray ra && i < ra.Count && ra[i] is JsonObject rp2 && rp2["panels"] is JsonArray np2
            && j < np2.Count) return (np2, j);
        return null;
    }

    private static bool TryResolveVariable(JsonObject root, string? varKey, out JsonObject variable)
    {
        variable = null!;
        if (string.IsNullOrWhiteSpace(varKey) || varKey!.Length < 2 || varKey[0] != 'v') return false;
        if (!TryParseIndex(varKey[1..], out var i)) return false;
        var list = VarListNode(root);
        if (list is null || i >= list.Count || list[i] is not JsonObject v) return false;
        variable = v;
        return true;
    }

    /// <summary>templating.list-Knoten (null, wenn nicht vorhanden/kein Array).</summary>
    private static JsonArray? VarListNode(JsonObject root) =>
        (root["templating"] as JsonObject)?["list"] as JsonArray;

    private static JsonArray EnsurePanelsArray(JsonObject root)
    {
        if (root["panels"] is not JsonArray panels)
            root["panels"] = panels = new JsonArray();
        return panels;
    }

    private static JsonObject EnsureObject(JsonObject parent, string property)
    {
        if (parent[property] is not JsonObject obj)
            parent[property] = obj = new JsonObject();
        return obj;
    }

    private static JsonArray EnsureArray(this JsonObject parent, string property)
    {
        if (parent[property] is not JsonArray arr)
            parent[property] = arr = new JsonArray();
        return arr;
    }

    /// <summary>Naechste freie Panel-Id (max bestehender + 1; Ids sind nicht eindeutig,
    /// der Editor vergibt sie nur fuer Neu-Anlagen als Konvention).</summary>
    private static int NextPanelId(JsonObject root)
    {
        int max = 0;
        foreach (var (p, _) in ResolvePanels(root))
            if (NullableInt(p, "id") is int id && id > max) max = id;
        return max + 1;
    }

    private static GrafanaGridPos ReadGrid(JsonObject panel)
    {
        if (panel["gridPos"] is not JsonObject g) return GrafanaGridPos.Zero;
        return new GrafanaGridPos(IntOr(g, "x"), IntOr(g, "y"), IntOr(g, "w", 6), IntOr(g, "h", 8));
    }

    private static void RemoveGraphMode(JsonObject panel)
    {
        if (panel["options"] is JsonObject o) o.Remove("graphMode");
    }

    private static int TargetCount(JsonObject panel)
    {
        if (panel["targets"] is not JsonArray ts) return 0;
        int n = 0;
        foreach (var t in ts)
            if (t is JsonObject to && !string.IsNullOrWhiteSpace(S(to, "expr"))) n++;
        return n;
    }

    private static bool IsRow(JsonObject p) =>
        string.Equals(S(p, "type"), "row", StringComparison.OrdinalIgnoreCase);

    // --- JSON-Lese-Helfer (werfen nie) ------------------------------------------

    private static string? S(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static string? S(JsonNode? node, string property) => node is JsonObject o ? S(o[property]) : null;

    private static bool Bool(JsonNode? node, string property) =>
        node is JsonObject o && o[property] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static int IntOr(JsonObject obj, string property, int dflt = 0)
    {
        if (obj[property] is not JsonValue v) return dflt;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return dflt;
    }

    private static int? NullableInt(JsonObject obj, string property)
    {
        if (obj[property] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }

    private static double? NullableDouble(JsonObject obj, string property)
    {
        if (obj[property] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return null;
    }

    private static bool TryParseIndex(string? segment, out int index) =>
        int.TryParse(segment, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out index)
        && index >= 0 && index < 500;   // Bombenschutz: keine Mega-Indizes
}

