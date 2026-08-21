using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.MvcSample.Store;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Heimdall.MvcSample.Traffic;

/// <summary>
/// Hintergrund-Service, der kontinuierlich realistische Aufrufe gegen die eigene
/// WebAPI absetzt (CRUD + Bestell-Flow), sodass im Heimdall-Dashboard echte
/// Server-Spans mit echten Controller/Action-Tags landen und der
/// Controller/Endpoint-Drilldown unter /otel/endpoints sichtbar wird.
/// </summary>
public sealed class TrafficService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TrafficService> _log;
    private readonly Random _rng = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TrafficService(IHttpClientFactory http, ILogger<TrafficService> log)
    {
        _http = http;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Kurz warten, bis Kestrel hochgefahren ist.
        await Task.Delay(1500, stoppingToken);
        _log.LogInformation("TrafficService: beginne Live-Traffic gegen /api/*.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await StepAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Traffic-Schritt fehlgeschlagen (ignoriert).");
            }
            await Task.Delay(250 + _rng.Next(0, 500), stoppingToken);
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var client = _http.CreateClient("mvc");
        switch (_rng.Next(0, 10))
        {
            case 0: await GetAsync(client, "api/kunden", ct); break;
            case 1: await GetByIdAsync(client, "api/kunden", PickId(), ct); break;
            case 2: await PostKundeAsync(client, ct); break;
            case 3: await GetAsync(client, "api/adressen", ct); break;
            case 4: await GetAsync(client, "api/artikel", ct); break;
            case 5: await GetByIdAsync(client, "api/artikel", PickId(), ct); break;
            case 6: await GetAsync(client, "api/bestellungen", ct); break;
            case 7: await PostBestellungAsync(client, ct); break;
            case 8: await PutKundeAsync(client, PickId(), ct); break;
            default: await DeleteKundeAsync(client, PickId(), ct); break;
        }
    }

    private int PickId() => _rng.Next(1, 6);

    private static async Task GetAsync(HttpClient c, string path, CancellationToken ct)
        => await c.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);

    private static async Task GetByIdAsync(HttpClient c, string path, int id, CancellationToken ct)
        => await c.GetAsync($"{path}/{id}", HttpCompletionOption.ResponseHeadersRead, ct);

    private async Task PostKundeAsync(HttpClient c, CancellationToken ct)
    {
        int n = _rng.Next(100, 9999);
        var k = new { name = $"Auto {n}", email = $"auto{n}@example.com" };
        await c.PostAsJsonAsync("api/kunden", k, _json, ct);
    }

    private async Task PutKundeAsync(HttpClient c, int id, CancellationToken ct)
    {
        var k = new { id, name = $"Update {id}", email = $"upd{ id}@example.com" };
        await c.PutAsJsonAsync($"api/kunden/{id}", k, _json, ct);
    }

    private async Task DeleteKundeAsync(HttpClient c, int id, CancellationToken ct)
        => await c.DeleteAsync($"api/kunden/{id}", ct);

    private async Task PostBestellungAsync(HttpClient c, CancellationToken ct)
    {
        // Order-Flow: ein Kunde, seine Adresse, 1–3 Artikel-Positionen.
        int kundeId = PickId();
        var bestellung = new
        {
            kundeId,
            adresseId = kundeId,   // Saat: Adresse i gehört zu Kunde i
            positionen = BuildPositionen(),
        };
        await c.PostAsJsonAsync("api/bestellungen", bestellung, _json, ct);
    }

    private List<BestellPosition> BuildPositionen()
    {
        int n = _rng.Next(1, 4);
        var pos = new List<BestellPosition>();
        for (int i = 0; i < n; i++)
            pos.Add(new BestellPosition(PickId(), _rng.Next(1, 4)));
        return pos;
    }
}