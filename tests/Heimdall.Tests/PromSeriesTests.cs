using System;
using System.Collections.Generic;
using System.Linq;
using Heimdall.Prometheus;
using Xunit;

namespace Heimdall.Tests;

// ---------------------------------------------------------------------------
// Tests fuer den SeriesLabels-Fast-Path (Hebel 1): With() fuegt einen nicht
// vorhandenen Key per Binaersuche in das sortierte Paar-Array ein (statt
// SortedDictionary-Re-Sort). Invarianten: kanonisch sortiert, Fingerprint +
// Hash korrekt, No-Op bei gleichem Wert, Replace bei anderem Wert.
// ---------------------------------------------------------------------------

public class PromSeriesTests
{
    private static SeriesLabels Labels(params (string k, string v)[] pairs)
        => new SeriesLabels(pairs.Select(p => new KeyValuePair<string, string>(p.k, p.v)));

    [Fact]
    public void With_NeuerKey_FuegtSortiertEin()
    {
        var l = Labels(("a", "1"), ("c", "3"));
        var r = l.With("b", "2");

        Assert.Equal(new[] { "a", "b", "c" }, r.Keys.ToArray());
        Assert.Equal("2", r["b"]);
        Assert.Equal("1", r["a"]);
        Assert.Equal("3", r["c"]);
        // Original unveraendert (immutable).
        Assert.False(l.ContainsKey("b"));
    }

    [Fact]
    public void With_NeuerKey_AmAnfangUndEnde()
    {
        var l = Labels(("b", "2"), ("c", "3"));
        var front = l.With("a", "1");
        Assert.Equal(new[] { "a", "b", "c" }, front.Keys.ToArray());

        var back = l.With("z", "26");
        Assert.Equal(new[] { "b", "c", "z" }, back.Keys.ToArray());
    }

    [Fact]
    public void With_GleicherWert_NoOp()
    {
        var l = Labels(("a", "1"), ("b", "2"));
        var r = l.With("b", "2");
        Assert.Same(l, r);   // immutable No-Op
    }

    [Fact]
    public void With_AndererWert_Replace()
    {
        var l = Labels(("a", "1"), ("b", "2"));
        var r = l.With("b", "99");
        Assert.Equal("99", r["b"]);
        Assert.Equal("1", r["a"]);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void With_FingerprintUndHash_Kanonisch()
    {
        var l = Labels(("b", "2"), ("a", "1"));
        var r = l.With("c", "3");

        // Kanonisch sortiert: a="1",b="2",c="3".
        Assert.Equal("a=\"1\",b=\"2\",c=\"3\"", r.Fingerprint);
        // Gleichheit ueber Fingerprint+Hash: gleiche Labels in anderer
        // Konstruktions-Reihenfolge sind wertgleich.
        var other = Labels(("a", "1"), ("b", "2"), ("c", "3"));
        Assert.Equal(other, r);
        Assert.Equal(other.GetHashCode(), r.GetHashCode());
    }

    [Fact]
    public void With_LeeresSet()
    {
        var l = SeriesLabels.Empty;
        var r = l.With("__name__", "orders_total");
        Assert.Single(r);
        Assert.Equal("orders_total", r["__name__"]);
        Assert.Equal("__name__=\"orders_total\"", r.Fingerprint);
    }

    [Fact]
    public void With_HistogrammBucketExpansion_ErgebnisIdentisch()
    {
        // Simuliert ExpandPoint: __name__-Basis einmal, dann le-Buckets.
        var baseLabels = Labels(("service_name", "shop"), ("http.method", "GET"));
        var bucketBase = baseLabels.With("__name__", "http.server.request.duration_bucket");
        var le1 = bucketBase.With("le", "0.1");
        var le2 = bucketBase.With("le", "+Inf");

        Assert.Equal("http.server.request.duration_bucket", le1.Name);
        Assert.Equal("0.1", le1["le"]);
        Assert.Equal("+Inf", le2["le"]);
        // Kanonisch sortiert: __name__ < http.method < le < service_name.
        Assert.Equal("__name__", le1.Keys.First());
        Assert.Equal("service_name", le1.Keys.Last());
    }
}
