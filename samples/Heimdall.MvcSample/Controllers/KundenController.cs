using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.MvcSample.Store;
using Microsoft.AspNetCore.Mvc;

namespace Heimdall.MvcSample.Controllers;

/// <summary>
/// CRUD für Kunden. Attribut-Routing <c>api/[controller]</c> → Route
/// <c>api/Kunden</c>; Heimdall.AspNetCore taggt den Server-Span mit
/// <c>aspnetmvc.controller=Kunden</c> + jeweiliger Action → Drilldown.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class KundenController : ControllerBase
{
    private readonly DataStore _store;
    public KundenController(DataStore store) => _store = store;

    [HttpGet]
    public async Task<ActionResult<List<Kunde>>> GetAll(CancellationToken ct)
    {
        await _store.LatencyAsync("list", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Kunden) return Ok(_store.Kunden.ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Kunde>> GetById(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("get", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Kunden)
        {
            var k = _store.Kunden.Find(x => x.Id == id);
            return k is null ? NotFound() : Ok(k);
        }
    }

    [HttpPost]
    public async Task<ActionResult<Kunde>> Create(Kunde kunde, CancellationToken ct)
    {
        await _store.LatencyAsync("insert", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        var created = kunde with { Id = _store.NextKunde() };
        lock (_store.Kunden) _store.Kunden.Add(created);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Kunde kunde, CancellationToken ct)
    {
        await _store.LatencyAsync("update", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Kunden)
        {
            var idx = _store.Kunden.FindIndex(x => x.Id == id);
            if (idx < 0) return NotFound();
            _store.Kunden[idx] = kunde with { Id = id };
        }
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("delete", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Kunden)
        {
            var removed = _store.Kunden.RemoveAll(x => x.Id == id);
            return removed > 0 ? NoContent() : NotFound();
        }
    }
}