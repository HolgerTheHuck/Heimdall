// Heimdall — minimales Progressive-Enhancement-JS (Vanilla, kein Build-Step, kein npm).
//
// Fünf Bereiche (alle ohne JS optional — die Seite bleibt nutzbar):
//   1. Hover-Tooltips auf Linienchart-Punkten (.hmd-chart-pt mit data-t/data-v/data-label).
//   2. Crosshair + synchrone Wertanzeige auf Liniencharts (SVG mit .hmd-chart-data-Payload).
//   3. Brushing/Zoom: horizontaler Drag im Chart → Redirect mit from/to (Unix-ns).
//   4. Zeitpicker: datetime-local → Unix-ns (hidden from/to) beim Submit; Auto-Refresh.
//   5. Signal-Band (Übersicht): Crosshair + Tooltip je Lane (.hmd-band-chart mit data-vals).
//
// Charts/Punkte werden server-seitig als SVG gerendert (HeimdallCharting); dieses Script
// reichert sie nur client-seitig an. Theme-Tokens (--hmd-*) kommen aus dem Heimdall-CSS.

(function () {
    "use strict";

    var SVGNS = "http://www.w3.org/2000/svg";

    // === Punkt-Hover-Tooltips =============================================

    var tooltip = null;
    var currentTarget = null;

    function ensureTooltip() {
        if (tooltip) return tooltip;
        tooltip = document.createElement("div");
        tooltip.className = "hmd-tooltip";
        tooltip.setAttribute("role", "tooltip");
        tooltip.style.position = "fixed";
        tooltip.style.zIndex = "9000";
        tooltip.style.pointerEvents = "none";
        tooltip.style.display = "none";
        tooltip.style.padding = ".4rem .6rem";
        tooltip.style.background = "var(--hmd-panel, var(--hmd-bg, #0d1117))";
        tooltip.style.color = "var(--hmd-fg, #e6edf3)";
        tooltip.style.border = "1px solid var(--hmd-border, #30363d)";
        tooltip.style.borderRadius = "4px";
        tooltip.style.fontFamily = "ui-monospace, Consolas, monospace";
        tooltip.style.fontSize = ".76rem";
        tooltip.style.lineHeight = "1.45";
        tooltip.style.whiteSpace = "nowrap";
        tooltip.style.boxShadow = "0 2px 8px rgba(0,0,0,.35)";
        document.body.appendChild(tooltip);
        return tooltip;
    }

    function fmtTime(ns) {
        var ms = Number(ns) / 1e6;
        if (!isFinite(ms)) return ns;
        try { return new Date(ms).toLocaleString(); } catch (_) { return ns; }
    }

    function fmtValue(v) {
        var n = Number(v);
        if (!isFinite(n)) return v;
        var abs = Math.abs(n);
        if (abs !== 0 && (abs >= 1e9 || abs < 1e-3)) return n.toExponential(2);
        return n.toLocaleString(undefined, { maximumFractionDigits: 4 });
    }

    // Nanosekunden-Dauer -> menschlich (µs/ms/s), wie HeimdallFmt.Dur server-seitig.
    function fmtDur(ns) {
        var n = Number(ns);
        if (!isFinite(n) || n < 0) return ns;
        if (n < 1e3) return n + " ns";
        var us = n / 1e3;
        if (us < 1e3) return us.toFixed(2) + " µs";
        var ms = us / 1e3;
        if (ms < 1e3) return ms.toFixed(2) + " ms";
        return (ms / 1e3).toFixed(3) + " s";
    }

    function esc(s) {
        return String(s == null ? "" : s)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }

    function show(target, clientX, clientY) {
        var tip = ensureTooltip();
        var label = target.getAttribute("data-label") || "";
        var t = target.getAttribute("data-t") || "";
        var v = target.getAttribute("data-v") || "";
        var isDur = target.classList && target.classList.contains("hmd-waterfall-bar");
        tip.innerHTML =
            (label ? "<div style=\"color:var(--hmd-accent,#58a6ff);font-weight:600\">" + esc(label) + "</div>" : "") +
            "<div>" + esc(fmtTime(t)) + "</div>" +
            "<div>" + esc(isDur ? fmtDur(v) : fmtValue(v)) + "</div>";
        tip.style.display = "block";
        position(clientX, clientY);
    }

    function position(clientX, clientY) {
        if (!tooltip) return;
        var pad = 12;
        var tw = tooltip.offsetWidth, th = tooltip.offsetHeight;
        var vw = window.innerWidth, vh = window.innerHeight;
        var x = clientX + pad, y = clientY + pad;
        if (x + tw + pad > vw) x = clientX - tw - pad;
        if (y + th + pad > vh) y = clientY - th - pad;
        if (x < pad) x = pad;
        if (y < pad) y = pad;
        tooltip.style.left = x + "px";
        tooltip.style.top = y + "px";
    }

    function hide() {
        if (tooltip) tooltip.style.display = "none";
        currentTarget = null;
    }

    function pointFromEvent(e) {
        return e.target && e.target.closest ? e.target.closest(".hmd-chart-pt") : null;
    }

    document.addEventListener("pointerover", function (e) {
        var el = pointFromEvent(e);
        if (!el) return;
        currentTarget = el;
        show(el, e.clientX, e.clientY);
    });
    document.addEventListener("pointermove", function (e) {
        if (currentTarget) position(e.clientX, e.clientY);
        updateCrosshair(e);
        updateBand(e);
    });
    document.addEventListener("pointerout", function (e) {
        var el = pointFromEvent(e);
        if (!el || el !== currentTarget) return;
        hide();
    });
    document.addEventListener("DOMContentLoaded", hide);

    // === Crosshair + Brushing (Liniencharts mit .hmd-chart-data) =========

    function chartFromEvent(e) {
        return e.target && e.target.closest ? e.target.closest("svg.hmd-chart") : null;
    }

    function parseChartData(svg) {
        if (svg._hmdData !== undefined) return svg._hmdData;
        var node = svg.querySelector(".hmd-chart-data");
        if (!node) { svg._hmdData = null; return null; }
        try { svg._hmdData = JSON.parse(node.textContent || ""); }
        catch (_) { svg._hmdData = null; }
        return svg._hmdData;
    }

    function ensureCross(svg) {
        if (svg._hmdCross) return svg._hmdCross;
        var line = document.createElementNS(SVGNS, "line");
        line.setAttribute("class", "hmd-crosshair");
        line.setAttribute("y1", "0");
        line.style.display = "none";
        svg.appendChild(line);
        svg._hmdCross = line;

        var tip = document.createElement("div");
        tip.className = "hmd-crosshair-tip";
        tip.style.position = "fixed";
        tip.style.zIndex = "9001";
        tip.style.pointerEvents = "none";
        tip.style.display = "none";
        tip.style.padding = ".4rem .6rem";
        tip.style.background = "var(--hmd-panel, #161b22)";
        tip.style.color = "var(--hmd-fg, #e6edf3)";
        tip.style.border = "1px solid var(--hmd-border, #30363d)";
        tip.style.borderRadius = "4px";
        tip.style.fontSize = ".76rem";
        tip.style.boxShadow = "0 2px 8px rgba(0,0,0,.35)";
        tip.style.maxWidth = "20rem";
        document.body.appendChild(tip);
        svg._hmdCrossTip = tip;
        return svg._hmdCross;
    }

    function toSvgX(svg, clientX) {
        var rect = svg.getBoundingClientRect();
        if (!rect.width) return null;
        var vb = svg.viewBox && svg.viewBox.baseVal;
        var vw = vb && vb.width ? vb.width : rect.width;
        return (clientX - rect.left) / rect.width * vw;
    }

    function nearestPts(data, svgX) {
        // Pro Serie der Punkt mit svg-x am nächsten an svgX.
        var out = [];
        for (var i = 0; i < data.series.length; i++) {
            var s = data.series[i];
            if (!s.pts || !s.pts.length) continue;
            var best = s.pts[0], bestD = Math.abs(s.pts[0][0] - svgX);
            for (var j = 1; j < s.pts.length; j++) {
                var d = Math.abs(s.pts[j][0] - svgX);
                if (d < bestD) { bestD = d; best = s.pts[j]; }
            }
            out.push({ label: s.label, color: s.color, x: best[0], y: best[1], t: best[2], v: best[3] });
        }
        return out;
    }

    function updateCrosshair(e) {
        var svg = chartFromEvent(e);
        if (!svg) return;
        var data = parseChartData(svg);
        if (!data || !data.geo) return;
        var svgX = toSvgX(svg, e.clientX);
        if (svgX == null) return;
        var g = data.geo;
        // Nur innerhalb der Plot-Fläche reagieren.
        if (svgX < g.padLeft || svgX > g.padLeft + g.plotW) { hideCross(svg); return; }

        var near = nearestPts(data, svgX);
        if (!near.length) { hideCross(svg); return; }

        var line = ensureCross(svg);
        line.setAttribute("x1", near[0].x);
        line.setAttribute("x2", near[0].x);
        line.setAttribute("y1", g.padTop);
        line.setAttribute("y2", g.padTop + g.plotH);
        line.style.display = "";

        var tip = svg._hmdCrossTip;
        var html = "<div style=\"color:var(--hmd-dim,#8b949e);margin-bottom:.2rem\">" + esc(fmtTime(near[0].t)) + "</div>";
        for (var i = 0; i < near.length; i++) {
            html += "<div style=\"display:flex;gap:.4rem;align-items:center\">"
                + "<span style=\"display:inline-block;width:10px;height:10px;border-radius:2px;background:" + near[i].color + "\"></span>"
                + "<span style=\"color:var(--hmd-dim,#8b949e)\">" + esc(near[i].label) + "</span>"
                + "<strong style=\"margin-left:auto\">" + esc(fmtValue(near[i].v)) + "</strong></div>";
        }
        tip.innerHTML = html;
        tip.style.display = "block";
        var pad = 12, tw = tip.offsetWidth, th = tip.offsetHeight;
        var vw = window.innerWidth, vh = window.innerHeight;
        var x = e.clientX + pad, y = e.clientY + pad;
        if (x + tw + pad > vw) x = e.clientX - tw - pad;
        if (y + th + pad > vh) y = e.clientY - th - pad;
        if (x < pad) x = pad; if (y < pad) y = pad;
        tip.style.left = x + "px"; tip.style.top = y + "px";
    }

    function hideCross(svg) {
        if (svg._hmdCross) svg._hmdCross.style.display = "none";
        if (svg._hmdCrossTip) svg._hmdCrossTip.style.display = "none";
    }

    document.addEventListener("pointerout", function (e) {
        var svg = chartFromEvent(e);
        if (svg) {
            // Verlässt der Pointer das SVG komplett → Crosshair weg.
            var rt = e.relatedTarget;
            if (!rt || !svg.contains(rt)) hideCross(svg);
        }
        var chart = bandChartFromEvent(e);
        if (chart) {
            var rt2 = e.relatedTarget;
            if (!rt2 || !chart.contains(rt2)) hideBand(chart);
        }
    });

    // --- Brushing/Zoom: Drag → from/to-Redirect --------------------------
    var brush = null; // { svg, startX (client), svgX0, rect }

    document.addEventListener("pointerdown", function (e) {
        if (e.button !== 0) return;
        var svg = chartFromEvent(e);
        if (!svg) return;
        var data = parseChartData(svg);
        if (!data || !data.geo) return;
        var svgX = toSvgX(svg, e.clientX);
        if (svgX == null) return;
        var g = data.geo;
        if (svgX < g.padLeft || svgX > g.padLeft + g.plotW) return;
        var rect = document.createElementNS(SVGNS, "rect");
        rect.setAttribute("class", "hmd-brush");
        rect.setAttribute("y", g.padTop);
        rect.setAttribute("height", g.plotH);
        rect.setAttribute("x", svgX);
        rect.setAttribute("width", 0);
        svg.appendChild(rect);
        brush = { svg: svg, data: data, startClient: e.clientX, svgX0: svgX, rect: rect };
        // Verhindere Text-Auswahl während des Drag.
        e.preventDefault();
    });

    document.addEventListener("pointermove", function (e) {
        if (!brush) return;
        var g = brush.data.geo;
        var svgX = toSvgX(brush.svg, e.clientX);
        if (svgX == null) return;
        var x0 = Math.min(brush.svgX0, svgX), x1 = Math.max(brush.svgX0, svgX);
        x0 = Math.max(g.padLeft, x0); x1 = Math.min(g.padLeft + g.plotW, x1);
        brush.rect.setAttribute("x", x0);
        brush.rect.setAttribute("width", Math.max(0, x1 - x0));
    });

    document.addEventListener("pointerup", function (e) {
        if (!brush) return;
        var b = brush; brush = null;
        b.rect.remove();
        var dxClient = Math.abs(e.clientX - b.startClient);
        if (dxClient < 8) return; // Klick, kein Brush
        var g = b.data.geo;
        var svgX0 = toSvgX(b.svg, b.startClient);
        var svgX1 = toSvgX(b.svg, e.clientX);
        if (svgX0 == null || svgX1 == null) return;
        var x0 = Math.min(svgX0, svgX1), x1 = Math.max(svgX0, svgX1);
        var inv = function (sx) { return g.xMin + (sx - g.padLeft) / g.plotW * (g.xMax - g.xMin); };
        var t0 = Math.round(inv(Math.max(g.padLeft, x0)));
        var t1 = Math.round(inv(Math.min(g.padLeft + g.plotW, x1)));
        if (t1 <= t0) return;
        var params = new URLSearchParams(location.search);
        params.set("from", String(t0));
        params.set("to", String(t1));
        params.delete("preset");
        location.search = params.toString();
    });

    // === Signal-Band (Übersicht): Crosshair + Tooltip je Lane ============
    // Der Server liefert je Lane 60 Minuten-Buckets als SVG (Linie/Fläche über
    // Null-Basis, Endpunkt-Dot als HTML) plus data-vals/data-max — hier kommt
    // nur der Hover-Layer dazu: vertikale Fadenkreuz-Linie im SVG, runder
    // Punkt + Tooltip als HTML-Spans (kreisrund trotz preserveAspectRatio=
    // "none"). Ohne JS bleibt die Lane voll lesbar (Wert + Rate im Meta-Block).

    function bandChartFromEvent(e) {
        return e.target && e.target.closest ? e.target.closest(".hmd-band-chart") : null;
    }

    function bandVals(chart) {
        if (chart._hmdVals !== undefined) return chart._hmdVals;
        var raw = chart.getAttribute("data-vals") || "";
        var vals = [], parts = raw.split(",");
        for (var i = 0; i < parts.length; i++) {
            var n = Number(parts[i]);
            if (isFinite(n)) vals.push(n);
        }
        chart._hmdVals = vals;
        return vals;
    }

    function ensureBandEls(chart) {
        if (chart._hmdBand !== undefined) return;
        var svg = chart.querySelector("svg");
        if (!svg) { chart._hmdBand = null; return; }
        var vb = svg.viewBox && svg.viewBox.baseVal;
        var vw = vb && vb.width ? vb.width : 600;
        var vh = vb && vb.height ? vb.height : 56;
        var line = document.createElementNS(SVGNS, "line");
        line.setAttribute("class", "hmd-band-xhair");
        line.setAttribute("x1", "0");
        line.setAttribute("x2", "0");
        line.setAttribute("y1", "0");
        line.setAttribute("y2", String(vh));
        svg.appendChild(line);
        var dot = document.createElement("span");
        dot.className = "hmd-band-hoverdot";
        chart.appendChild(dot);
        var tip = document.createElement("span");
        tip.className = "hmd-band-tip";
        chart.appendChild(tip);
        chart._hmdBand = { line: line, dot: dot, tip: tip, vw: vw, vh: vh };
    }

    function hideBand(chart) {
        var b = chart._hmdBand;
        if (!b) return;
        b.line.style.visibility = "hidden";
        b.dot.style.visibility = "hidden";
        b.tip.style.visibility = "hidden";
    }

    function updateBand(e) {
        var chart = bandChartFromEvent(e);
        if (!chart) return;
        var vals = bandVals(chart);
        if (!vals || vals.length < 2) return;
        ensureBandEls(chart);
        var b = chart._hmdBand;
        if (!b) return;
        var rect = chart.getBoundingClientRect();
        if (!rect.width) return;
        var fx = (e.clientX - rect.left) / rect.width;
        if (fx < 0 || fx > 1) { hideBand(chart); return; }
        var i = Math.round(fx * (vals.length - 1));
        var v = vals[i];
        // Punkt-Höhe wie server (HeimdallCharting: Null-Basis, 3px Padding oben/unten).
        var max = Number(chart.getAttribute("data-max")) || 1;
        var y = b.vh - 3 - (v / max) * (b.vh - 6);
        b.line.setAttribute("x1", String(fx * b.vw));
        b.line.setAttribute("x2", String(fx * b.vw));
        b.line.style.visibility = "visible";
        b.dot.style.left = (fx * 100) + "%";
        b.dot.style.top = (y / b.vh * 100) + "%";
        b.dot.style.visibility = "visible";
        var ago = (chart.getAttribute("data-ago") || "{0}")
            .replace("{0}", String(vals.length - 1 - i));
        b.tip.textContent = ago + " · " + fmtValue(v) + " " + (chart.getAttribute("data-lbl") || "");
        b.tip.style.visibility = "visible";
        // Tooltip horizontal klemmen, damit er an den Bandrändern nicht hinausläuft.
        var tw = b.tip.offsetWidth;
        var leftPx = Math.min(Math.max(fx * rect.width, tw / 2 + 2), rect.width - tw / 2 - 2);
        b.tip.style.left = leftPx + "px";
    }

    // === Zeitpicker: datetime-local → Unix-ns + Auto-Refresh ============

    function isoToNs(value) {
        if (!value) return null;
        var d = new Date(value);
        if (isNaN(d.getTime())) return null;
        // datetime-local ist lokale Zeit → getTime() liefert UTC-ms.
        return String(d.getTime() * 1e6);
    }

    document.addEventListener("submit", function (e) {
        var form = e.target;
        if (!form || form.tagName !== "FORM") return;
        var fromIso = form.querySelector('input[name="from-iso"]');
        var toIso = form.querySelector('input[name="to-iso"]');
        if (!fromIso && !toIso) return;
        var set = function (isoEl, hiddenName) {
            if (!isoEl || !isoEl.value) return;
            var ns = isoToNs(isoEl.value);
            if (ns == null) return;
            var hidden = form.querySelector('input[name="' + hiddenName + '"]');
            if (!hidden) {
                hidden = document.createElement("input");
                hidden.type = "hidden";
                hidden.name = hiddenName;
                form.appendChild(hidden);
            }
            hidden.value = ns;
        };
        set(fromIso, "from");
        set(toIso, "to");
        // Wenn explizite Zeiten gesendet werden, preset verwerfen.
        if (fromIso && fromIso.value) { var p = form.querySelector('input[name="preset"], button[name="preset"]'); if (p && p.type === "hidden") p.remove(); }
        // Leere from/to-hidden-Felder entfernen, damit sie nicht als `from=&to=` (leer)
        // gesendet werden — sonst unschöne URLs und (ohne serverseitige Toleranz) ein
        // Bindungsfehler bei Preset-Submit. Gefüllte Felder bleiben (siehe set() oben).
        ["from", "to"].forEach(function (n) {
            var h = form.querySelector('input[name="' + n + '"]');
            if (h && !h.value) h.remove();
        });
    }, true);

    function startAutoRefresh() {
        var params = new URLSearchParams(location.search);
        var r = params.get("refresh");
        if (!r) return;
        var m = /^(\d+)(s|m)$/.exec(r);
        if (!m) return;
        var secs = parseInt(m[1], 10) * (m[2] === "m" ? 60 : 1);
        if (secs < 3) return;
        var iv = setInterval(function () { location.reload(); }, secs * 1000);
        var badge = document.createElement("div");
        badge.className = "hmd-refresh-badge";
        badge.setAttribute("role", "status");
        badge.setAttribute("aria-live", "polite");
        badge.textContent = "Auto-Refresh " + r;
        var stop = document.createElement("button");
        stop.type = "button";
        stop.className = "hmd-btn hmd-btn-danger";
        stop.textContent = "stoppen";
        stop.addEventListener("click", function () {
            clearInterval(iv);
            params.delete("refresh");
            location.search = params.toString();
        });
        badge.appendChild(stop);
        (document.querySelector(".hmd-main") || document.body).appendChild(badge);
    }

    // Refresh-Select: bei Änderung direkt den refresh-Query-Param setzen + neu laden
    // (unabhängig vom restlichen Filter-Form; die anderen Parameter bleiben erhalten).
    function bindRefreshSelect() {
        var sel = document.querySelector('select[name="refresh"]');
        if (!sel || sel._hmdBound) return;
        sel._hmdBound = true;
        sel.addEventListener("change", function () {
            var p = new URLSearchParams(location.search);
            if (sel.value) p.set("refresh", sel.value); else p.delete("refresh");
            location.search = p.toString();
        });
    }

    function initTimePicker() {
        startAutoRefresh();
        bindRefreshSelect();
    }

    // === Lazy Dashboard-Panels ============================================
    // Die Dashboard-Shell rendert sofort Platzhalter-Kacheln (kein PromQL vor
    // dem ersten Byte). Hier werden sie per Vanilla-JS vom Per-Panel-Endpoint
    // nachgeladen: IntersectionObserver (falls verfügbar) fetcht bei Viewport-
    // Nähe, sonst sofort (Progressive Enhancement — ohne IO alle laden, aber
    // die Shell steht schon). Nach dem outerHTML-Swap greift das bestehende
    // Chart-Enhancement per Event-Delegation automatisch (kein Re-Init).

    function panelUrl(base, uid, idx) {
        return base + "/dashboards/" + encodeURIComponent(uid) + "/panel/" + idx + location.search;
    }

    function loadPanel(panel, url) {
        if (panel._hmdLoading) return;
        panel._hmdLoading = true;
        fetch(url, { headers: { "Accept": "text/html" }, credentials: "same-origin" })
            .then(function (res) { if (!res.ok) throw new Error("HTTP " + res.status); return res.text(); })
            .then(function (html) { panel.outerHTML = html; })
            .catch(function () {
                panel._hmdLoading = false;
                panel.classList.remove("hmd-gpanel--loading");
                var body = panel.querySelector(".hmd-gpanel-body");
                if (body) {
                    body.innerHTML = '<p class="hmd-empty hmd-err-text" role="status">' +
                        esc(panel.getAttribute("data-hmd-failed") || "Panel could not be loaded.") + "</p>";
                }
            });
    }

    function initLazyPanels() {
        var grid = document.querySelector(".hmd-grid[data-hmd-base][data-hmd-uid]");
        if (!grid) return;
        var base = grid.getAttribute("data-hmd-base") || "";
        var uid = grid.getAttribute("data-hmd-uid") || "";
        var panels = grid.querySelectorAll(".hmd-gpanel[data-hmd-panel]");
        if (!panels.length) return;

        if ("IntersectionObserver" in window) {
            var io = new IntersectionObserver(function (entries) {
                for (var i = 0; i < entries.length; i++) {
                    if (entries[i].isIntersecting) {
                        var p = entries[i].target;
                        io.unobserve(p);
                        loadPanel(p, panelUrl(base, uid, p.getAttribute("data-hmd-panel")));
                    }
                }
            }, { rootMargin: "200px" });
            for (var k = 0; k < panels.length; k++) io.observe(panels[k]);
        } else {
            for (var j = 0; j < panels.length; j++)
                loadPanel(panels[j], panelUrl(base, uid, panels[j].getAttribute("data-hmd-panel")));
        }
    }

    function initAll() { initTimePicker(); initLazyPanels(); }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initAll);
    } else {
        initAll();
    }
})();