using Heimdall.Blazor;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer die Zeitbereich-Auflösung (HeimdallRange): Preset → Unix-ns-Fenster,
/// explizite from/to überschreiben Preset, „all" → unbegrenzt, Default-Fallback.
/// </summary>
public class HeimdallRangeTests
{
    private const long S  = 1_000_000_000L;       // 1 Sekunde
    private const long H  = 3_600L * S;            // 1 Stunde
    private const long D  = 24L * H;               // 1 Tag
    private const long Now = 100_000L * S;         // beliebiger fester Zeitpunkt

    [Fact]
    public void Resolve_Preset1h_LiefertLetzteStunde()
    {
        var r = HeimdallRange.Resolve("1h", null, null, Now);
        Assert.Equal(Now - H, r.From!.Value);
        Assert.Equal(Now, r.To!.Value);
    }

    [Fact]
    public void Resolve_Preset15m_LiefertLetzteViertelstunde()
    {
        var r = HeimdallRange.Resolve("15m", null, null, Now);
        Assert.Equal(Now - 15 * 60 * S, r.From!.Value);
        Assert.Equal(Now, r.To!.Value);
    }

    [Fact]
    public void Resolve_Preset24h_Und7d()
    {
        var d24 = HeimdallRange.Resolve("24h", null, null, Now);
        Assert.Equal(Now - D, d24.From!.Value);
        Assert.Equal(Now, d24.To!.Value);
        var d7 = HeimdallRange.Resolve("7d", null, null, Now);
        Assert.Equal(Now - 7 * D, d7.From!.Value);
        Assert.Equal(Now, d7.To!.Value);
    }

    [Fact]
    public void Resolve_PresetAll_LiefertUnbegrenzt()
    {
        var r = HeimdallRange.Resolve("all", null, null, Now);
        Assert.Null(r.From);
        Assert.Null(r.To);
    }

    [Fact]
    public void Resolve_ExpliziteFromToUeberschreibtPreset()
    {
        var r = HeimdallRange.Resolve("1h", 123L, 456L, Now);
        Assert.Equal(123L, r.From!.Value);
        Assert.Equal(456L, r.To!.Value);
    }

    [Fact]
    public void Resolve_NurFrom_GibtFromUndNullTo()
    {
        var r = HeimdallRange.Resolve("1h", 123L, null, Now);
        Assert.Equal(123L, r.From!.Value);
        Assert.Null(r.To);
    }

    [Fact]
    public void Resolve_LeerFallback_Default1h()
    {
        var r = HeimdallRange.Resolve(null, null, null, Now);
        Assert.Equal(Now - H, r.From!.Value);
        Assert.Equal(Now, r.To!.Value);
    }

    [Fact]
    public void Resolve_LeerFallback_Custom24h()
    {
        var r = HeimdallRange.Resolve(null, null, null, Now, fallbackPreset: "24h");
        Assert.Equal(Now - D, r.From!.Value);
        Assert.Equal(Now, r.To!.Value);
    }

    [Fact]
    public void Resolve_UnbekannterPreset_FaelltAuf1h()
    {
        var r = HeimdallRange.Resolve("does-not-exist", null, null, Now);
        Assert.Equal(Now - H, r.From!.Value);
        Assert.Equal(Now, r.To!.Value);
    }

    [Fact]
    public void Presets_EnthaeltAlleSchluessel()
    {
        var keys = new System.Collections.Generic.HashSet<string>();
        foreach (var p in HeimdallRange.Presets) keys.Add(p.Key);
        Assert.Contains("15m", keys);
        Assert.Contains("1h", keys);
        Assert.Contains("24h", keys);
        Assert.Contains("7d", keys);
        Assert.Contains("all", keys);
        // „all" hat keine Spanne.
        Assert.Null(HeimdallRange.Presets.First(p => p.Key == "all").SpanNanos);
    }
}