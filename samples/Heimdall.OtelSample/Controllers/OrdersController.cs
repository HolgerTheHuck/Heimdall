using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.OtelSample.Store;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Heimdall.OtelSample.Controllers;

/// <summary>
/// Bestellungen incl. Order-Flow: POST validiert die Positionen, liest die
/// Produkt-Preise, berechnet die Summe, bucht den Lagerbestand und legt die
/// Bestellung an. So entsteht pro Action eine eigene Latenzspur (Order ist
/// langsamer als einfache Selects) — gut sichtbar im Dauer-Histogramm.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController : ControllerBase
{
    private readonly DataStore _store;
    private readonly ILogger<OrdersController> _log;

    public OrdersController(DataStore store, ILogger<OrdersController> log)
    { _store = store; _log = log; }

    public sealed record CreateOrderRequest(int CustomerId, List<OrderLine> Lines);

    [HttpGet]
    public async Task<ActionResult<List<Order>>> GetAll(CancellationToken ct)
    {
        await _store.LatencyAsync("list", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Orders) return Ok(_store.Orders.ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Order>> GetById(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("get", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Orders)
        {
            var o = _store.Orders.Find(x => x.Id == id);
            return o is null ? NotFound() : Ok(o);
        }
    }

    /// <summary>Order anlegen: Validierung + Preis-Selects + Summe + Lagerbuchung.</summary>
    [HttpPost]
    public async Task<ActionResult<Order>> Create(CreateOrderRequest req, CancellationToken ct)
    {
        await _store.LatencyAsync("order", ct);
        if (_store.MaybeFault())
        {
            _log.LogError("orders: simulated fault creating order for customer {Customer}", req.CustomerId);
            return StatusCode(500, "simulated fault");
        }
        if (req.Lines is null || req.Lines.Count == 0)
            return BadRequest("Bestellung braucht mindestens eine Position.");

        lock (_store.Products) lock (_store.Orders)
        {
            decimal total = 0;
            var lines = new List<OrderLine>();
            foreach (var l in req.Lines)
            {
                var p = _store.Products.Find(x => x.Id == l.ProductId);
                if (p is null) return NotFound($"Produkt {l.ProductId} nicht gefunden.");
                if (p.Stock < l.Qty) return Conflict($"Produkt {l.ProductId}: nur {p.Stock} auf Lager.");
                total += p.Price * l.Qty;
                lines.Add(new OrderLine(l.ProductId, l.Qty));
            }
            // Lager buchen.
            for (int i = 0; i < _store.Products.Count; i++)
                foreach (var l in lines)
                    if (_store.Products[i].Id == l.ProductId)
                        _store.Products[i] = _store.Products[i] with { Stock = _store.Products[i].Stock - l.Qty };

            var order = new Order(_store.NextOrder(), req.CustomerId, lines, total, "Offen");
            _store.Orders.Add(order);
            _log.LogInformation("orders: created order {Id} (customer={Customer}, total={Total}, lines={Lines})",
                order.Id, order.CustomerId, order.Total, order.Lines.Count);
            return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
        }
    }

    /// <summary>Status einer Bestellung ändern (z. B. Offen → Bezahlt → Versendet).</summary>
    [HttpPut("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] string status, CancellationToken ct)
    {
        await _store.LatencyAsync("insert", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Orders)
        {
            var idx = _store.Orders.FindIndex(o => o.Id == id);
            if (idx < 0) return NotFound();
            _store.Orders[idx] = _store.Orders[idx] with { Status = status };
        }
        _log.LogInformation("orders: {Id} -> {Status}", id, status);
        return NoContent();
    }
}