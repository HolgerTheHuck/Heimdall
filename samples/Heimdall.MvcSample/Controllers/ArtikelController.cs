using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.MvcSample.Store;
using Microsoft.AspNetCore.Mvc;

namespace Heimdall.MvcSample.Controllers;

/// <summary>CRUD für Artikel (Katalog).</summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ArtikelController : ControllerBase
{
    private readonly DataStore _store;
    public ArtikelController(DataStore store) => _store = store;

    [HttpGet]
    public async Task<ActionResult<List<Artikel>>> GetAll(CancellationToken ct)
    {
        await _store.LatencyAsync("list", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Artikel) return Ok(_store.Artikel.ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Artikel>> GetById(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("get", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Artikel)
        {
            var a = _store.Artikel.Find(x => x.Id == id);
            return a is null ? NotFound() : Ok(a);
        }
    }

    [HttpPost]
    public async Task<ActionResult<Artikel>> Create(Artikel artikel, CancellationToken ct)
    {
        await _store.LatencyAsync("insert", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        var created = artikel with { Id = _store.NextArtikel() };
        lock (_store.Artikel) _store.Artikel.Add(created);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Artikel artikel, CancellationToken ct)
    {
        await _store.LatencyAsync("update", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Artikel)
        {
            var idx = _store.Artikel.FindIndex(x => x.Id == id);
            if (idx < 0) return NotFound();
            _store.Artikel[idx] = artikel with { Id = id };
        }
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("delete", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Artikel)
        {
            var removed = _store.Artikel.RemoveAll(x => x.Id == id);
            return removed > 0 ? NoContent() : NotFound();
        }
    }
}