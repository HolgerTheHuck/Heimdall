using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Heimdall.Storage.SQLite;

/// <summary>
/// JSON-Spalten als Brotli-komprimierte BLOBs (Format v2, <c>user_version=2</c>).
///
///Attrs/Events/Links/Resource-JSONs dominieren das DB-Volumen (dehnige
/// OTel-Attribut-Payloads, je Zeile dupliziert); Brotli drückt sie um typische
/// 80–95 %. Format: <c>0x01</c>-Präfix + Brotli-Stream — das Präfix ist ein
/// Format-Marker (künftige Codecs bekommen eigene Magic-Bytes), nicht ein
/// Dual-Format-Gate: <b>geschrieben wird immer komprimiert</b> (auch leere
/// Objekte — ein Format, kein Pfad-Verzweigung), gelesen wird streng BLOB-only
/// (keine Kompatibilität mit Format v1/TEXT — Bestands-DBs werden verworfen,
/// kein Downgrade). <c>heim_metric_series</c> bleibt ausdrücklich TEXT
/// (byte-gleiche UNIQUE-Equality/Dedup der Serien), ebenso FTS (indiziert nur
/// name/body).
/// </summary>
internal static class JsonBlob
{
    /// <summary>Format-Marker: BLOB beginnt mit diesem Byte, danach Brotli-Stream.</summary>
    internal const byte MagicBrotli = 0x01;

    /// <summary>
    /// JSON → Brotli-BLOB. null → null (Spalte bleibt NULL). CompressionLevel.
    /// Optimal ist bei .NET-Brotli fest Quality 4 — deterministisch und schnell
    /// genug für den Ingest-Pfad (µs-Bereich je Attribut-JSON).
    /// </summary>
    internal static byte[]? Encode(string? json)
    {
        if (json is null) return null;
        var raw = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream(raw.Length / 2 + 16);
        ms.WriteByte(MagicBrotli);
        using (var bs = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            bs.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// BLOB → JSON. Streng BLOB-only: DBNull/null → null, sonst muss das BLOB
    /// mit <see cref="MagicBrotli"/> beginnen — alles andere (TEXT aus einer
    /// Fremd-/Alt-DB) wirft, statt still Daten zu verlieren.
    /// </summary>
    internal static string? Decode(object? v)
    {
        if (v is null or DBNull) return null;
        if (v is byte[] blob && blob.Length > 1 && blob[0] == MagicBrotli)
        {
            using var ms = new MemoryStream(blob, 1, blob.Length - 1);
            using var bs = new BrotliStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream(blob.Length * 4);
            bs.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }
        throw new InvalidDataException(
            "heimdall-Format v2: JSON-Spalte erwartet Brotli-BLOB (0x01-Präfix) — " +
            "Gefunden: " + (v is byte[] b ? $"BLOB {b.Length} B ohne Magic" : v.GetType().Name) + ".");
    }
}