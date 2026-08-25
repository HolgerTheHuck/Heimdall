using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// Prom-Runtime-Typen: Label-Sets, Samples, Vektoren (Instant), Matrizen
// (Range) sowie Skalar-/String-Ergebnisse. Interne Zeitstempel sind Unix-
// Millisekunden (long); die HTTP-Schicht wandelt in Prom-Float-Sekunden um.
// ---------------------------------------------------------------------------

/// <summary>
/// Sortiertes, unveränderliches Label-Set mit kanonischem Fingerprint
/// (<c>k="v",k2="v2"</c>) und Cache-HashCode — wird beim Gruppieren und
/// Vektor-Matching stark als Dictionary-Key benutzt.
/// </summary>
public sealed class SeriesLabels : IReadOnlyDictionary<string, string>, IEquatable<SeriesLabels>
{
    private readonly KeyValuePair<string, string>[] _pairs;
    private readonly Dictionary<string, string> _lookup;
    private readonly int _hash;
    private readonly string _fingerprint;

    /// <summary>Erzeugt ein sortiertes Label-Set aus den gegebenen Paaren (null = leer).</summary>
    public SeriesLabels(IEnumerable<KeyValuePair<string, string>>? labels)
    {
        var list = new List<KeyValuePair<string, string>>(labels ?? Array.Empty<KeyValuePair<string, string>>());
        // Doppelte Keys: letzter gewinnt (Prom-Verhalten). Sortiert nach Key.
        var byKey = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in list) byKey[kv.Key] = kv.Value;
        _pairs = new KeyValuePair<string, string>[byKey.Count];
        int i = 0;
        foreach (var kv in byKey) _pairs[i++] = kv;
        _lookup = new Dictionary<string, string>(byKey, StringComparer.Ordinal);
        _fingerprint = BuildFingerprint(_pairs);
        _hash = BuildHash(_pairs);
    }

    /// <summary>Vertrauens-Konstruktor fuer den Fast-Path in <see cref="With"/>:
    /// uebernimmt bereits sortierte Paare + Lookup + Fingerprint + Hash ohne
    /// Re-Sort/Re-Build (der Aufrufer garantiert die Invarianten).</summary>
    private SeriesLabels(KeyValuePair<string, string>[] pairs, Dictionary<string, string> lookup,
                         string fingerprint, int hash)
    {
        _pairs = pairs; _lookup = lookup; _fingerprint = fingerprint; _hash = hash;
    }

    private static string BuildFingerprint(KeyValuePair<string, string>[] pairs)
    {
        var sb = new StringBuilder(pairs.Length * 16);
        foreach (var kv in pairs)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(kv.Key).Append("=\"").Append(Escape(kv.Value)).Append('"');
        }
        return sb.ToString();
    }

    private static int BuildHash(KeyValuePair<string, string>[] pairs)
    {
        unchecked
        {
            int h = 17;
            foreach (var kv in pairs) h = h * 31 + (kv.Key.GetHashCode() ^ kv.Value.GetHashCode());
            return h;
        }
    }

    /// <summary>Leeres Label-Set (keine Labels).</summary>
    public static SeriesLabels Empty { get; } = new SeriesLabels(null);

    /// <summary>Kanonischer Fingerprint (<c>k="v",k2="v2"</c>).</summary>
    public string Fingerprint => _fingerprint;
    /// <summary><c>true</c>, wenn das Set ein <c>__name__</c>-Label traegt.</summary>
    public bool HasName => _lookup.TryGetValue("__name__", out _);
    /// <summary>Wert des <c>__name__</c>-Labels oder <c>null</c>.</summary>
    public string? Name => _lookup.TryGetValue("__name__", out var n) ? n : null;

    /// <summary>Gibt ein neues Set zurueck, in dem <paramref name="key"/> auf <paramref name="value"/> gesetzt ist.
    /// Fast-Path fuer den haeufigsten Fall (Key nicht vorhanden, z. B. le-Bucket-Expansion oder
    /// __name__-Anhaengen): fuegt den Key per Binaersuche in das sortierte Paar-Array ein, statt das
    /// Dict neu zu sortieren. Bei unveraendertem Wert wird <c>this</c> zurueckgegeben (immutable).</summary>
    public SeriesLabels With(string key, string value)
    {
        if (!_lookup.ContainsKey(key))
        {
            // Binaersuche: Einfuegeposition im sortierten Paar-Array.
            int lo = 0, hi = _pairs.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (StringComparer.Ordinal.Compare(_pairs[mid].Key, key) < 0) lo = mid + 1; else hi = mid;
            }
            var pairs = new KeyValuePair<string, string>[_pairs.Length + 1];
            Array.Copy(_pairs, 0, pairs, 0, lo);
            pairs[lo] = new KeyValuePair<string, string>(key, value);
            Array.Copy(_pairs, lo, pairs, lo + 1, _pairs.Length - lo);
            var lookup = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i++) lookup[pairs[i].Key] = pairs[i].Value;
            return new SeriesLabels(pairs, lookup, BuildFingerprint(pairs), BuildHash(pairs));
        }
        if (string.Equals(_lookup[key], value, StringComparison.Ordinal)) return this;
        var dict = new Dictionary<string, string>(_lookup, StringComparer.Ordinal) { [key] = value };
        return new SeriesLabels(dict);
    }

    /// <summary>Gibt ein neues Set ohne <paramref name="key"/> zurueck (oder dieses, falls nicht vorhanden).</summary>
    public SeriesLabels Without(string key)
    {
        if (!_lookup.ContainsKey(key)) return this;
        var dict = new Dictionary<string, string>(_lookup, StringComparer.Ordinal);
        dict.Remove(key);
        return new SeriesLabels(dict);
    }

    /// <summary>Labelset ohne __name__ (Default-Match-Key fuer Vektor-Operatoren).</summary>
    public SeriesLabels WithoutName() => Without("__name__");

    /// <summary>Nur die angegebenen Labels (on(...)-Match-Key).</summary>
    public SeriesLabels Project(IReadOnlyList<string> labels)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in labels) if (_lookup.TryGetValue(k, out var v)) dict[k] = v;
        return new SeriesLabels(dict);
    }

    /// <summary>Alle Labels ausser __name__ und den angegebenen (ignoring(...)-Match-Key).</summary>
    public SeriesLabels WithoutNameAnd(IReadOnlyList<string> exclude)
    {
        var ex = new HashSet<string>(exclude, StringComparer.Ordinal) { "__name__" };
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _lookup) if (!ex.Contains(kv.Key)) dict[kv.Key] = kv.Value;
        return new SeriesLabels(dict);
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\\' || c == '"') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Wertgleichheit ueber Fingerprint (gleiche Labels in kanonischer Reihenfolge).</summary>
    public bool Equals(SeriesLabels? other) => other is not null && _hash == other._hash && _fingerprint == other._fingerprint;
    /// <summary>Wertgleichheit (delegiert an <see cref="Equals(SeriesLabels?)"/>).</summary>
    public override bool Equals(object? obj) => obj is SeriesLabels s && Equals(s);
    /// <summary>Cache-HashCode (kompatibel mit <see cref="Equals(SeriesLabels?)"/>).</summary>
    public override int GetHashCode() => _hash;

    // --- IReadOnlyDictionary<string,string> ---
    /// <summary>Wert des Labels <paramref name="key"/> (wirft, falls nicht vorhanden).</summary>
    public string this[string key] => _lookup[key];
    /// <summary>Sortierte Label-Namen.</summary>
    public IEnumerable<string> Keys => _lookup.Keys;
    /// <summary>Label-Werte in Sortier-Reihenfolge der Keys.</summary>
    public IEnumerable<string> Values => _lookup.Values;
    /// <summary>Anzahl Labels.</summary>
    public int Count => _lookup.Count;
    /// <summary><c>true</c>, wenn <paramref name="key"/> vorhanden.</summary>
    public bool ContainsKey(string key) => _lookup.ContainsKey(key);
    /// <summary>Versucht den Wert zu <paramref name="key"/> zu holen.</summary>
    public bool TryGetValue(string key, out string value)
    {
        bool ok = _lookup.TryGetValue(key, out var v);
        value = v ?? string.Empty;
        return ok;
    }
    /// <summary>Enumeriert die Label-Paare in Sortier-Reihenfolge.</summary>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        for (int i = 0; i < _pairs.Length; i++) yield return _pairs[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Ein Instant-Sample: Labels + Zeitstempel (ms) + Wert.</summary>
public sealed record Sample(SeriesLabels Labels, long TimestampMs, double Value);

/// <summary>Ein Range-Punkt innerhalb einer Matrix-Serie.</summary>
public sealed record RangePoint(long TimestampMs, double Value);

/// <summary>Eine Matrix-Serie: Labels + Punkte im Fenster.</summary>
public sealed record RangeSeries(SeriesLabels Labels, IReadOnlyList<RangePoint> Points);

/// <summary>Instant-Vektor (Liste von Samples zu einem T).</summary>
public sealed record InstantVector(IReadOnlyList<Sample> Samples)
{
    /// <summary>Leerer Instant-Vektor.</summary>
    public static InstantVector Empty { get; } = new InstantVector(Array.Empty<Sample>());
}

/// <summary>Matrix (Liste von Range-Serien).</summary>
public sealed record Matrix(IReadOnlyList<RangeSeries> Series)
{
    /// <summary>Leere Matrix.</summary>
    public static Matrix Empty { get; } = new Matrix(Array.Empty<RangeSeries>());
}

/// <summary>Skalar-Ergebnis (Wert + T).</summary>
public sealed record ScalarResult(double Value, long TimestampMs);

/// <summary>String-Ergebnis (Wert + T).</summary>
public sealed record StringResult(string Value, long TimestampMs);

/// <summary>Art des Eval-Ergebnisses.</summary>
public enum PromResultKind
{
    /// <summary>Skalar (Zahl + T).</summary>
    Scalar,
    /// <summary>String (Text + T).</summary>
    String,
    /// <summary>Instant-Vektor (Samples zu einem T).</summary>
    Vector,
    /// <summary>Matrix (Range-Serien ueber mehrere T).</summary>
    Matrix
}

/// <summary>Union aller Eval-Ergebnisarten.</summary>
public sealed class PromResult
{
    /// <summary>Art des Ergebnisses.</summary>
    public PromResultKind Kind { get; }
    /// <summary>Skalar-Ergebnis, falls <see cref="Kind"/> == <see cref="PromResultKind.Scalar"/>.</summary>
    public ScalarResult? Scalar { get; }
    /// <summary>String-Ergebnis, falls <see cref="Kind"/> == <see cref="PromResultKind.String"/>.</summary>
    public StringResult? String { get; }
    /// <summary>Instant-Vektor, falls <see cref="Kind"/> == <see cref="PromResultKind.Vector"/>.</summary>
    public InstantVector? Vector { get; }
    /// <summary>Matrix, falls <see cref="Kind"/> == <see cref="PromResultKind.Matrix"/>.</summary>
    public Matrix? Matrix { get; }

    private PromResult(PromResultKind kind, ScalarResult? s, StringResult? str, InstantVector? v, Matrix? m)
    { Kind = kind; Scalar = s; String = str; Vector = v; Matrix = m; }

    /// <summary>Erzeugt ein Skalar-Ergebnis.</summary>
    public static PromResult Of(ScalarResult s) => new(PromResultKind.Scalar, s, null, null, null);
    /// <summary>Erzeugt ein String-Ergebnis.</summary>
    public static PromResult Of(StringResult s) => new(PromResultKind.String, null, s, null, null);
    /// <summary>Erzeugt einen Instant-Vektor.</summary>
    public static PromResult Of(InstantVector v) => new(PromResultKind.Vector, null, null, v, null);
    /// <summary>Erzeugt eine Matrix.</summary>
    public static PromResult Of(Matrix m) => new(PromResultKind.Matrix, null, null, null, m);
}