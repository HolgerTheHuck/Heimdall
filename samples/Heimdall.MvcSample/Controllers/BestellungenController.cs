using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heimdall.MvcSample.Store;
using Microsoft.AspNetCore.Mvc;

namespace Heimdall.MvcSample.Controllers;

/// <summary>
/// Bestellungen incl. „Order"-Flow: POST validiert Kunde + Adresse, liest die
/// Artikel-Preise (Selects), berechnet die Summe, bucht den Lagerbestand und legt
/// die Bestellung an. So entsteht im Heimdall-Drilldown pro Action eine eigene
/// Latenzspur (Order ist langsamer als einfache Selects).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class BestellungenController : ControllerBase
{
    private readonly DataStore _store;
    public BestellungenController(DataStore store) => _store = store;

    public sealed record CreateBestellungRequest(int KundeId, int AdresseId, List<BestellPosition> Positionen);

    [HttpGet]
    public async Task<ActionResult<List<Bestellung>>> GetAll(CancellationToken ct)
    {
        await _store.LatencyAsync("list", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Bestellungen) return Ok(_store.Bestellungen.ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Bestellung>> GetById(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("get", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Bestellungen)
        {
            var b = _store.Bestellungen.Find(x => x.Id == id);
            return b is null ? NotFound() : Ok(b);
        }
    }

    /// <summary>Order anlegen: Validierung + Preis-Selects + Summe + Lagerbuchung.</summary>
    [HttpPost]
    public async Task<ActionResult<Bestellung>> Create(CreateBestellungRequest req, CancellationToken ct)
    {
        await _store.LatencyAsync("order", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        if (req.Positionen is null || req.Positionen.Count == 0)
            return BadRequest("Bestellung braucht mindestens eine Position.");

        lock (_store.Kunden) lock (_store.Adressen) lock (_store.Artikel) lock (_store.Bestellungen)
        {
            var kunde = _store.Kunden.Find(k => k.Id == req.KundeId);
            if (kunde is null) return NotFound($"Kunde {req.KundeId} nicht gefunden.");
            var adresse = _store.Adressen.Find(a => a.Id == req.AdresseId && a.KundeId == req.KundeId);
            if (adresse is null) return NotFound($"Adresse {req.AdresseId} für Kunde {req.KundeId} nicht gefunden.");

            decimal gesamt = 0;
            var positionen = new List<BestellPosition>();
            foreach (var p in req.Positionen)
            {
                var artikel = _store.Artikel.Find(a => a.Id == p.ArtikelId);
                if (artikel is null) return NotFound($"Artikel {p.ArtikelId} nicht gefunden.");
                if (artikel.Lager < p.Menge) return Conflict($"Artikel {p.ArtikelId}: nur {artikel.Lager} auf Lager.");
                gesamt += artikel.Preis * p.Menge;
                positionen.Add(new BestellPosition(p.ArtikelId, p.Menge));
            }
            // Lager buchen.
            for (int i = 0; i < _store.Artikel.Count; i++)
                foreach (var p in positionen)
                    if (_store.Artikel[i].Id == p.ArtikelId)
                        _store.Artikel[i] = _store.Artikel[i] with { Lager = _store.Artikel[i].Lager - p.Menge };

            var bestellung = new Bestellung(_store.NextBestellung(), req.KundeId, req.AdresseId,
                positionen, gesamt, "Offen");
            _store.Bestellungen.Add(bestellung);
            return CreatedAtAction(nameof(GetById), new { id = bestellung.Id }, bestellung);
        }
    }

    /// <summary>Status einer Bestellung ändern (z. B. Offen → Bezahlt → Versendet).</summary>
    [HttpPut("{id}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] string status, CancellationToken ct)
    {
        await _store.LatencyAsync("update", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Bestellungen)
        {
            var idx = _store.Bestellungen.FindIndex(b => b.Id == id);
            if (idx < 0) return NotFound();
            _store.Bestellungen[idx] = _store.Bestellungen[idx] with { Status = status };
        }
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _store.LatencyAsync("delete", ct);
        if (_store.MaybeFault()) return StatusCode(500, "simulated fault");
        lock (_store.Bestellungen)
        {
            var removed = _store.Bestellungen.RemoveAll(b => b.Id == id);
            return removed > 0 ? NoContent() : NotFound();
        }
    }
}