using System.Collections.Generic;

namespace Heimdall;

/// <summary>
/// Schluessel/Wert-Attribut der Telemetrie (Span-Attribut, Log-Feld, Metrik-Label,
/// Resource-Eigenschaft). Werte sind primitive Skalare (string, long, double,
/// bool, DateTime, byte[]) oder JSON-serialisierbar; das Storage fasst sie als
/// JSON zusammen.
/// </summary>
public readonly record struct HAttribute(string Key, object? Value)
{
    /// <summary>True, wenn der Wert null oder ein leerer String ist (wird beim
    /// Schreiben unterdrueckt).</summary>
    public bool IsEmpty => Value is null || (Value is string s && s.Length == 0);
}

/// <summary>Helfer fuer Attribut-Listen.</summary>
public static class HAttributes
{
    public static readonly IReadOnlyList<HAttribute> Empty =
        System.Array.Empty<HAttribute>();

    public static IReadOnlyList<HAttribute> Of(params HAttribute[] attrs) =>
        attrs is null || attrs.Length == 0 ? Empty : attrs;
}