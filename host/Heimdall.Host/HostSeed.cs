using Heimdall;
using Heimdall.Blazor.Alerts;
using Heimdall.Blazor.Grafana;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.Host;

/// <summary>
/// Demo- und Beispiel-Seeding für den Stand-alone-Host. Beide Routinen sind idempotent
/// bzw. rein additiv — die persistente DB wird (im Gegensatz zum alten SelfHost) NICHT
/// gelöscht. Gesteuert via <see cref="HeimdallHostOptions.SeedDemoData"/> bzw.
/// <see cref="HeimdallDashboardsStoreOptions.SeedExample"/>. Portiert aus
/// <c>samples/Heimdall.SelfHost/Program.cs</c>.
/// </summary>
internal static class HeimdallSeeder
{
    /// <summary>
    /// Besät den Sink mit Demo-Daten (Spans, Logs mit TraceIds, steigende Counter +
    /// Latenz-Histogramm mit wanderndem p95, MVC-Controller/Endpoint-Drilldown-Saat).
    /// rein additiv — mehrfacher Aufruf dupliziert die Saat (nur einmal nach Start vorgesehen).
    /// </summary>
    public static void SeedDemoData(IHeimdallSink sink)
    {
        var res = new HResource(new[] { new HAttribute("service.name", "shop") });
        var scope = new HScope("api", "1.0", Array.Empty<HAttribute>());
        var t0 = DateTimeOffset.UtcNow.UtcTicks - 5_000_000; // Demo-Zeitstempel
        long Ns(long ticks) => (ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) * 100L;

        var traceA = Tid(0xa1, 1);
        var traceB = Tid(0xb2, 1);
        sink.WriteSpans(new[]
        {
            new HSpan(traceA, Sid(1), null, "checkout cart", HSpanKind.Server,
                Ns(t0), Ns(t0 + 800_000), HStatusCode.Ok, null,
                new[] { new HAttribute("http.method", "GET"), new HAttribute("http.route", "/cart") },
                Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(), res, scope),
            new HSpan(traceA, Sid(2), Sid(1), "db.query orders", HSpanKind.Client,
                Ns(t0 + 100_000), Ns(t0 + 700_000), HStatusCode.Error, "timeout",
                new[] { new HAttribute("db.system", "sqlite") },
                Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(), res, scope),
            new HSpan(traceB, Sid(3), null, "user login", HSpanKind.Server,
                Ns(t0 + 1_000_000), Ns(t0 + 1_400_000), HStatusCode.Ok, null,
                Array.Empty<HAttribute>(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(), res, scope),
        });

        sink.WriteLogs(new[]
        {
            new HLogRecord(Ns(t0), HSeverity.Info, "INFO", "order placed for alice", traceA, Sid(1),
                Array.Empty<HAttribute>(), res, scope),
            new HLogRecord(Ns(t0 + 600_000), HSeverity.Error, "ERROR", "db timeout in query", traceA, Sid(2),
                Array.Empty<HAttribute>(), res, scope),
            new HLogRecord(Ns(t0 + 1_100_000), HSeverity.Warn, "WARN", "slow login for bob", traceB, Sid(3),
                Array.Empty<HAttribute>(), res, scope),
        });

        // Steigende Last (calls/s) + variierende Fehler-Counter → Dashboard zeigt eine
        // steigende calls/s-Linie und eine schwankende Errorrate/Uptime.
        var baseTs = Ns(t0);
        int[] orders = { 10, 21, 33, 46, 60, 75, 91, 108, 126, 145 };           // Deltas 11..19/s
        int[] orderErrors = { 0, 0, 1, 1, 2, 3, 3, 4, 6, 7 };                   // kumulativ
        for (int i = 0; i < orders.Length; i++)
        {
            sink.WriteMetrics(new[]
            {
                new HMetricPoint("orders", "1", HMetricType.Sum, HTemporality.Cumulative, baseTs + i * 1_000_000_000L,
                    orders[i], null, null, null, null, null, null,
                    new[] { new HAttribute("region", "eu") }, res, scope),
                new HMetricPoint("orders.errors", "1", HMetricType.Sum, HTemporality.Cumulative, baseTs + i * 1_000_000_000L,
                    orderErrors[i], null, null, null, null, null, null,
                    new[] { new HAttribute("region", "eu") }, res, scope),
            });
        }

        // Antwortzeiten: http.server.request.duration als Histogramm (Delta) mit Standard-
        // OTel-Bucket-Schranken. Pro Sekunde wandert das p95-Bucket nach rechts → p95-Linie
        // steigt (~38 ms → ~750 ms); p50 bleibt deutlich darunter. Buckets (12): 11 Bounds.
        double[] bounds = { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 };
        int[] p95Bucket = { 3, 3, 4, 4, 5, 5, 6, 6, 7, 7 };   // Sekunde i → p95-Bucket-Index
        for (int i = 0; i < p95Bucket.Length; i++)
        {
            var counts = HistogramCounts(p95Bucket[i]);     // 100 Beob.; 90 unterhalb, 10 im p95-Bucket
            double sum = 0;
            for (int b = 0; b < counts.Length; b++)
            {
                double lo = b == 0 ? 0 : bounds[b - 1];
                double hi = b < bounds.Length ? bounds[b] : bounds[bounds.Length - 1];
                sum += (lo + hi) * 0.5 * counts[b];          // Bucket-Mitten-Summe (Approx.)
            }
            sink.WriteMetrics(new[]
            {
                new HMetricPoint("http.server.request.duration", "s", HMetricType.Histogram, HTemporality.Delta,
                    baseTs + i * 1_000_000_000L, sum, 100, sum, 0, bounds[p95Bucket[i]],
                    counts, bounds,
                    new[] { new HAttribute("http.route", "/cart") }, res, scope),
            });
        }

        // Controller/Endpoint-Drilldown-Saat: synthetische Server-Spans mit echten
        // aspnetmvc.controller/action-Attributen + http.route + http.response.status_code.
        SeedMvcSpans(sink, res, scope, baseTs);
    }

    /// <summary>
    /// Legt das Beispiel-Dashboard (grafana/heimdall-overview.json, wird ins Ausgabeverzeichnis
    /// kopiert) im dateibasierten Store ab, falls es dort noch nicht existiert. Idempotent.
    /// Schlägt fehl → UI läuft trotzdem (manuell importieren).
    /// </summary>
    public static void SeedExampleDashboard(IServiceProvider services)
    {
        try
        {
            var store = services.GetRequiredService<IGrafanaDashboardStore>();
            if (store.Get("heimdall-overview") is not null) return;   // schon vorhanden
            var path = Path.Combine(AppContext.BaseDirectory, "heimdall-overview.json");
            if (!File.Exists(path)) return;
            store.Save(File.ReadAllText(path));
        }
        catch { /* Seeding optional — nicht fatal */ }
    }

    /// <summary>
    /// Seedet zwei Beispiel-Alarmregeln (Metrik 5xx-Rate + Log-Fehler-Häufung) via
    /// IAlertRuleStore, falls noch keine Regel mit gleichem Namen existiert. Idempotent.
    /// Delegiert an <see cref="AlertDemoRules.Seed"/>. Schlägt fehl → /alerts läuft trotzdem.
    /// </summary>
    public static void SeedDemoAlerts(IServiceProvider services)
    {
        try
        {
            var store = services.GetRequiredService<IAlertRuleStore>();
            AlertDemoRules.Seed(store);
        }
        catch { /* Seeding optional — nicht fatal */ }
    }

    // 100 Beobachtungen: 90 verteilt auf die Buckets unterhalb des p95-Buckets, 10 im
    // p95-Bucket selbst → 95. Perzentil landet in der Mitte des p95-Buckets.
    private static long[] HistogramCounts(int p95Bucket)
    {
        var counts = new long[12];
        int baseC = 90 / p95Bucket, rem = 90 - baseC * p95Bucket;
        for (int b = 0; b < p95Bucket; b++)
            counts[b] = baseC + (b < rem ? 1 : 0);
        counts[p95Bucket] = 10;
        return counts;
    }

    // Synthetische MVC-Server-Spans für den Controller/Endpoint-Drilldown. Pro Sekunde
    // (10 s) je ein Span pro Endpoint mit aspnetmvc.controller/action + http.route +
    // http.response.status_code; Users.Get in Sekunde 5 wird zum 5xx-Fehler. Ein
    // zusätzlicher /cart-Span OHNE aspnetmvc.* pro Sekunde → Route-Parse-Fallback-Gruppe.
    private static void SeedMvcSpans(IHeimdallSink sink, HResource res, HScope scope, long baseTs)
    {
        var endpoints = new (string Controller, string Action, string Route, int BaseMs)[]
        {
            ("Users",  "Index",  "/api/users",     12),
            ("Users",  "Get",   "/api/users/{id}", 8),
            ("Orders", "List",  "/api/orders",    25),
            ("Orders", "Create","/api/orders",    40),
        };
        int seq = 100;
        for (int i = 0; i < 10; i++)
        {
            long ts = baseTs + i * 1_000_000_000L;
            foreach (var (ctrl, act, route, ms) in endpoints)
            {
                bool isError = ctrl == "Users" && act == "Get" && i == 5;
                long durNs = (long)((ms + i) * 1_000_000.0);
                sink.WriteSpans(new[] { SrvSpan(seq++, ts, durNs, ctrl, act, route, isError, isError ? 500 : 200, res, scope) });
            }
            // /cart ohne aspnetmvc.* → Controller aus Route-Parsen ("cart").
            sink.WriteSpans(new[] { SrvSpan(seq++, ts, 5_000_000, null, null, "/cart", false, 200, res, scope) });
        }
    }

    // Baut einen einzelnen Server-Span (eigene Trace) mit den MVC-Tags. ctrl/act null
    // → keine aspnetmvc.*-Attribute (Route-Parse-Pfad).
    private static HSpan SrvSpan(int seq, long startNs, long durNs, string? ctrl, string? act,
        string route, bool error, int httpStatus, HResource res, HScope scope)
    {
        var tid = Tid((byte)(seq >> 8), seq & 0xFF);
        var sid = Sid(seq & 0xFFFF);
        var attrs = new List<HAttribute>(4)
        {
            new("http.route", route),
            new("http.response.status_code", httpStatus),
        };
        if (ctrl is not null) attrs.Add(new HAttribute("aspnetmvc.controller", ctrl));
        if (act is not null) attrs.Add(new HAttribute("aspnetmvc.action", act));
        return new HSpan(tid, sid, null, route, HSpanKind.Server, startNs, startNs + durNs,
            error ? HStatusCode.Error : HStatusCode.Ok, error ? "boom" : null,
            attrs.ToArray(), Array.Empty<HSpanEvent>(), Array.Empty<HSpanLink>(), res, scope);
    }

    private static byte[] Tid(byte prefix, int last)
    {
        var b = new byte[16]; b[0] = prefix; b[15] = (byte)last; return b;
    }
    private static byte[] Sid(int n)
    {
        var b = new byte[8]; b[0] = (byte)(n >> 8); b[7] = (byte)n; return b;
    }
}