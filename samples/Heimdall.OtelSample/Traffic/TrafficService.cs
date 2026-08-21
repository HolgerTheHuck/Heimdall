using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.OtelSample.Store;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Heimdall.OtelSample.Traffic;

/// <summary>
/// Hintergrund-Service, der kontinuierlich realistische Aufrufe gegen die eigene
/// WebAPI absetzt (Produkte listen/holen, Bestellungen anlegen/status ändern),
/// sodass im Heimdall-Dashboard echte Server-Spans, http.server.request.duration-
/// Metriken (mit http_route/http_response_status_code/error_type) und ILogger-
/// Logs landen — das otel-dotnet-webapi-Dashboard füllt sich mit Live-Daten.
/// </summary>
public sealed class TrafficService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TrafficService> _log;
    private readonly Random _rng = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TrafficService(IHttpClientFactory http, ILogger<TrafficService> log)
    { _http = http; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1500, stoppingToken);   // Kestrel hochfahren lassen
        _log.LogInformation("TrafficService: beginne Live-Traffic gegen /api/*.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await StepAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Traffic-Schritt fehlgeschlagen (ignoriert).");
            }
            await Task.Delay(180 + _rng.Next(0, 420), stoppingToken);
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var client = _http.CreateClient("self");
        switch (_rng.Next(0, 10))
        {
            case 0: await GetAsync(client, "api/products", ct); break;
            case 1: await GetByIdAsync(client, "api/products", PickId(8), ct); break;
            case 2: await PostProductAsync(client, ct); break;
            case 3: await GetByIdAsync(client, "api/products", 999, ct); break;   // → 404
            case 4: await GetAsync(client, "api/orders", ct); break;
            case 5: await PostOrderAsync(client, ct); break;
            case 6: await GetByIdAsync(client, "api/orders", PickId(50), ct); break;
            case 7: await SetStatusAsync(client, PickId(50), ct); break;
            case 8: await PostOrderAsync(client, ct); break;                       // Order = langsamer
            default: await GetAsync(client, "api/products", ct); break;
        }
    }

    private int PickId(int max) => _rng.Next(1, max + 1);

    private static async Task GetAsync(HttpClient c, string path, CancellationToken ct)
        => await c.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);

    private static async Task GetByIdAsync(HttpClient c, string path, int id, CancellationToken ct)
        => await c.GetAsync($"{path}/{id}", HttpCompletionOption.ResponseHeadersRead, ct);

    private async Task PostProductAsync(HttpClient c, CancellationToken ct)
    {
        int n = _rng.Next(100, 9999);
        var p = new { name = $"Auto {n}", price = 9.90m + _rng.Next(0, 50), stock = _rng.Next(1, 80) };
        await c.PostAsJsonAsync("api/products", p, _json, ct);
    }

    private async Task PostOrderAsync(HttpClient c, CancellationToken ct)
    {
        var order = new
        {
            customerId = PickId(20),
            lines = BuildLines(),
        };
        await c.PostAsJsonAsync("api/orders", order, _json, ct);
    }

    private async Task SetStatusAsync(HttpClient c, int id, CancellationToken ct)
    {
        var status = _rng.Next(0, 3) switch { 0 => "Bezahlt", 1 => "Versendet", _ => "Storniert" };
        await c.PutAsJsonAsync($"api/orders/{id}/status", status, _json, ct);
    }

    private List<OrderLine> BuildLines()
    {
        int n = _rng.Next(1, 4);
        var lines = new List<OrderLine>();
        for (int i = 0; i < n; i++)
            lines.Add(new OrderLine(PickId(8), _rng.Next(1, 4)));
        return lines;
    }
}