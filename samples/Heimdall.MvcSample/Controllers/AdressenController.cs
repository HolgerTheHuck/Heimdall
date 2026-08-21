using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.MvcSample.Store;
using Microsoft.AspNetCore.Mvc;

namespace Heimdall.MvcSample.Controllers;

/// <summary>CRUD für Adressen (jede Adresse gehört zu einem Kunden).</summary>
[ApiController]
[Route("api/[controller]")]
public sealed class AdressenController : ControllerBase
{
    private readonly DataStore _store;
    public AdressenController(DataStore store) => _store = store;

    [HttpGet]
    public async Task<ActionResult<List<Adresse>>> GetAll(CancellationToken ct)
    {
        await _store.LatencyAsync("list", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Adressen) return Ok(_store.Adressen.ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Adresse>> GetById(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("get", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Adressen)
        {
            var a = _store.Adressen.Find(x => x.Id == id);
            return a is null ? NotFound() : Ok(a);
        }
    }

    [HttpPost]
    public async Task<ActionResult<Adresse>> Create(Adresse adresse, CancellationToken ct)
    {
        await _store.LatencyAsync("insert", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        var created = adresse with { Id = _store.NextAdresse() };
        lock (_store.Adressen) _store.Adressen.Add(created);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Adresse adresse, CancellationToken ct)
    {
        await _store.LatencyAsync("update", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Adressen)
        {
            var idx = _store.Adressen.FindIndex(x => x.Id == id);
            if (idx < 0) return NotFound();
            _store.Adressen[idx] = adresse with { Id = id };
        }
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("delete", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Adressen)
        {
            var removed = _store.Adressen.RemoveAll(x => x.Id == id);
            return removed > 0 ? NoContent() : NotFound();
        }
    }
}