# Heimdall → Grafana

Heimdall spricht die Prometheus-HTTP-API (`/api/v1/*`), daher lassen sich
importierte Grafana-Dashboards direkt gegen einen Heimdall-Host rendern — ohne
separaten Prometheus-Server.

## Datenquelle einrichten

1. Heimdall-Host starten (z. B. `dotnet run --project samples/Heimdall.SelfHost`).
   Die Prom-API liegt unter `http://localhost:5099/otel`.
2. In Grafana: **Connections → Data sources → Add data source → Prometheus**.
3. **URL**: `http://localhost:5099/otel` (das Präfix, unter dem
   `MapHeimdallPrometheus` gemountet ist — nicht die OTLP- oder Dashboard-Route).
   Authentifizierung: keine. Scrape-Intervall: z. B. `10s` (nur relevant für
   `/api/v1/metrics`; Queries nutzen die API direkt).
4. **Save & Test** — Grafana fragt `/api/v1/status/buildinfo` ab und meldet grün.

## Dashboard importieren

- **Dashboards → New → Import → Upload JSON file** → `heimdall-overview.json`.
- Beim Import als Datenquelle die oben angelegte Heimdall-Quelle wählen
  (Variable `DS_HEIMDALL`).

## Metriken

Heimdall exponiert OTel-Metriken **prom-konform** und zusätzlich als **rohen
OTel-Alias** (jeweils queryable):

| OTel-Name                     | Prom-Name                               | Typ        |
|-------------------------------|-----------------------------------------|------------|
| `orders`                      | `orders_total` (+ Alias `orders`)       | Counter    |
| `orders.errors`               | `orders_errors_total`                   | Counter    |
| `http.server.request.duration`| `http_server_request_duration_seconds_{bucket,sum,count}` | Histogram |
| `service.name`-Label          | `job`                                   | —          |

### RED-Metriken (aus Server-Spans abgeleitet)

Ohne eigene Meter-Instrumentierung leitet Heimdall aus Server-Spans die
klassischen Web-Metriken ab (`RedMetricsProvider`), gruppiert nach
`(job, http_route, http_method, http_response_status_code)`:

- `http_requests_total` — Counter (kumulativ, `rate()`-fähig)
- `http_request_duration_seconds_{bucket,sum,count}` — Histogramm
  (Buckets `[0.005,0.01,0.025,0.05,0.1,0.25,0.5,1,2.5,5,10,+Inf]`)

Typische Panel-Ausdrücke (siehe `heimdall-overview.json`):

```promql
# Requests / s nach Route
sum by (http_route) (rate(http_requests_total[5m]))

# Fehler-Rate % nach Route
sum by (http_route) (rate(http_requests_total{http_response_status_code=~"5.."}[5m]))
  / clamp_min(sum by (http_route) (rate(http_requests_total[5m])), 1) * 100

# p95-Latenz
histogram_quantile(0.95, sum by (le) (rate(http_request_duration_seconds_bucket[5m])))
```

## Hinweise

- Die PromQL-Engine ist handgeschrieben (breite Abdeckung, keine Drittabhängigkeit);
  exotische Funktionen können fehlen — bei Bedarf ergänzen.
- RED nutzt `IHeimdallQuery.ListSpans(Kind=Server, Limit=100000)`; für sehr große
  Fenster samplen (embedded-Scale: unkritisch).
- Storage-agnostisch: sowohl Walhalla- als auch SQLite-Backend implementieren
  `IHeimdallMetricSource`; beide liefern identische Ergebnisse (siehe
  `BackendParityMetricSourceTests`).