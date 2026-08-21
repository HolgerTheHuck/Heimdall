using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.OtelSample.Store;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Heimdall.OtelSample.Controllers;

/// <summary>
/// Produkte-Endpoint: list/get/create. Jede Action erzeugt über die simulierte
/// Latenz einen eigenen Dauer-Messwert und gelegentlich einen 5xx-Fehler, sodass
/// das otel-dotnet-webapi-Dashboard echte http.server.request.duration-Serien
/// (mit http_route + http_response_status_code + error_type) zu sehen bekommt.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ProductsController : ControllerBase
{
    private readonly DataStore _store;
    private readonly ILogger<ProductsController> _log;

    public ProductsController(DataStore store, ILogger<ProductsController> log)
    { _store = store; _log = log; }

    [HttpGet]
    public async Task<ActionResult<List<Product>>> GetAll(CancellationToken ct)
    {
        await _store.LatencyAsync("list", ct);
        if (_store.MaybeFault())
        {
            _log.LogError("products: simulated fault while listing {Count} products", _store.Products.Count);
            return StatusCode(500, "simulated fault");
        }
        _log.LogInformation("products: listed {Count} products", _store.Products.Count);
        lock (_store.Products) return Ok(_store.Products.ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Product>> GetById(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("get", ct);
        if (_store.MaybeFault())
        {
            _log.LogWarning("products: simulated fault fetching product {Id}", id);
            return StatusCode(500, "simulated fault");
        }
        lock (_store.Products)
        {
            var p = _store.Products.Find(x => x.Id == id);
            if (p is null) { _log.LogInformation("products: {Id} not found", id); return NotFound(); }
            return Ok(p);
        }
    }

    public sealed record CreateProductRequest(string Name, decimal Price, int Stock);

    [HttpPost]
    public async Task<ActionResult<Product>> Create(CreateProductRequest req, CancellationToken ct)
    {
        await _store.LatencyAsync("insert", ct);
        if (_store.MaybeFault())
        {
            _log.LogError("products: simulated fault creating product {Name}", req.Name);
            return StatusCode(500, "simulated fault");
        }
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Name fehlt.");
        lock (_store.Products)
        {
            int id = _store.Products.Count + 1;
            var p = new Product(id, req.Name, req.Price, req.Stock);
            _store.Products.Add(p);
            _log.LogInformation("products: created {Name} (id={Id}, stock={Stock})", p.Name, p.Id, p.Stock);
            return CreatedAtAction(nameof(GetById), new { id = p.Id }, p);
        }
    }
}