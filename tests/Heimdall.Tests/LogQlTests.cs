using System.Linq;
using Heimdall.Blazor.Grafana;
using Xunit;

namespace Heimdall.Tests;

/// <summary>
/// Tests fuer den LogQL-Teilparser (<see cref="LogQl"/>): Stream-Selector,
/// Zeilenfilter, Quoting (double/backtick), Modifier-Fehler.
/// </summary>
public class LogQlTests
{
    [Fact]
    public void Leer_LiefertEmpty()
    {
        Assert.Empty(LogQl.Parse(null).Stream);
        Assert.Empty(LogQl.Parse("").Stream);
        Assert.Empty(LogQl.Parse("   ").Lines);
    }

    [Fact]
    public void NurStreamSelector()
    {
        var q = LogQl.Parse("{service_name=\"shop\"}");
        Assert.Single(q.Stream);
        Assert.Equal("service_name", q.Stream[0].Key);
        Assert.Equal("=", q.Stream[0].Op);
        Assert.Equal("shop", q.Stream[0].Value);
        Assert.Empty(q.Lines);
    }

    [Fact]
    public void StreamSelector_MehrereMatcher_MitRegex()
    {
        var q = LogQl.Parse("{app=\"foo\", env=~\"prod.*\"}");
        Assert.Equal(2, q.Stream.Count);
        Assert.Equal("env", q.Stream[1].Key);
        Assert.Equal("=~", q.Stream[1].Op);
        Assert.Equal("prod.*", q.Stream[1].Value);
    }

    [Fact]
    public void Zeilenfilter_VierOps()
    {
        var q = LogQl.Parse("{} |= \"error\" != \"ignore\" |~ \"db.*\" !~ \"^ok\"");
        Assert.Equal(4, q.Lines.Count);
        Assert.Equal("|=", q.Lines[0].Op);
        Assert.Equal("error", q.Lines[0].Value);
        Assert.Equal("!=", q.Lines[1].Op);
        Assert.Equal("|~", q.Lines[2].Op);
        Assert.Equal("!~", q.Lines[3].Op);
    }

    [Fact]
    public void BacktickString_WirdErkannt()
    {
        // Loki-Raw-String (backtick) — ohne Escapes.
        var q = LogQl.Parse("{service_name=\"shop\"} |= ``");
        Assert.Single(q.Stream);
        Assert.Single(q.Lines);
        Assert.Equal("", q.Lines[0].Value);   // leerer Backtick-String = No-Op-Filter
    }

    [Fact]
    public void DoppelquoteEscapes_WerdenAufgeloest()
    {
        var q = LogQl.Parse("{} |= \"a\\\"b\\t\"");
        Assert.Single(q.Lines);
        Assert.Equal("a\"b\t", q.Lines[0].Value);
    }

    [Fact]
    public void StreamMitZeilenfilter_Kombiniert()
    {
        var q = LogQl.Parse("{service_name=~\"shop|billing\"} |= \"timeout\"");
        Assert.Single(q.Stream);
        Assert.Equal("=~", q.Stream[0].Op);
        Assert.Equal("shop|billing", q.Stream[0].Value);
        Assert.Single(q.Lines);
        Assert.Equal("timeout", q.Lines[0].Value);
    }

    [Fact]
    public void UnbalanciertesQuote_WirftNicht_LenientesErgebnis()
    {
        // Unbalanciertes Quote — der Parser ist lenient (liest bis String-Ende)
        // und wirft nie (der Renderer muss werferfrei bleiben).
        var q = LogQl.Parse("{service_name=\"shop");
        Assert.NotNull(q);
        Assert.Single(q.Stream);   // Matcher wird noch erkannt, Wert = Rest
    }

    [Fact]
    public void NurZeilenfilter_OhneStream()
    {
        var q = LogQl.Parse("|= \"error\"");
        Assert.Empty(q.Stream);
        Assert.Single(q.Lines);
        Assert.Equal("error", q.Lines[0].Value);
    }

    /// <summary>
    /// Beweist die LogQL->LogSearch-Abbildung, die LogsPage.razor::OnInitialized
    /// vornimmt: Stream-Selector -> <see cref="LogSearch.AttrFilters"/>,
    /// erstes nicht-leeres |= / |~ -> <see cref="LogSearch.Text"/>. Die Abbildung
    /// ist die Bruecke zwischen LogQL-Suchfeld und index-gestuetzter Attributsuche.
    /// </summary>
    [Fact]
    public void LogQlNachLogSearch_Abbildung_StreamZuAttrFilters_ZeileZuText()
    {
        var q = LogQl.Parse("{service.name=\"shop\", http.response.status_code!=\"200\"} |= \"timeout\"");

        // Stream-Selector -> AttrFilters (Op + Key 1:1, auch Punkt-Keys).
        var attrFilters = q.Stream
            .Select(m => new Heimdall.AttrFilter(m.Key, m.Op, m.Value))
            .ToList();
        Assert.Equal(2, attrFilters.Count);
        Assert.Equal("service.name", attrFilters[0].Key);
        Assert.Equal("=", attrFilters[0].Op);
        Assert.Equal("shop", attrFilters[0].Value);
        Assert.Equal("http.response.status_code", attrFilters[1].Key);
        Assert.Equal("!=", attrFilters[1].Op);

        // Erstes nicht-leeres |= / |~ -> Body-Text.
        string? body = null;
        foreach (var f in q.Lines)
            if ((f.Op == "|=" || f.Op == "|~") && !string.IsNullOrEmpty(f.Value)) { body = f.Value; break; }
        Assert.Equal("timeout", body);

        var s = new Heimdall.LogSearch { Text = body, AttrFilters = attrFilters, Limit = 200 };
        Assert.Equal("timeout", s.Text);
        Assert.Equal(2, s.AttrFilters!.Count);
    }

    [Fact]
    public void LogQlNachLogSearch_OhneZeilenfilter_TextBleibtNull()
    {
        var q = LogQl.Parse("{service.name=\"shop\"}");
        string? body = null;
        foreach (var f in q.Lines)
            if ((f.Op == "|=" || f.Op == "|~") && !string.IsNullOrEmpty(f.Value)) { body = f.Value; break; }
        Assert.Null(body);     // kein |= / |~ -> Body bleibt null (kein FTS-Filter)
    }
}