using System.Collections.Generic;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// PromQL-AST. Unveränderliche Records; der rekursive Abstieg (PromQLParser)
// erzeugt diese Knoten. Vollstaendige Grammatik (Auswertung staffelt sich in
// den Phasen: Phase 1 = VectorSelector/NumberLiteral/ParenExpr, Phase 2/3 =
// Range-Selektoren, Funktionen, Aggregationen, Vektor-Matching, Offset/@).
// ---------------------------------------------------------------------------

internal abstract record PromNode;

// --- Literal-Schalter -----------------------------------------------------
internal sealed record NumberLiteral(double Value) : PromNode;
internal sealed record StringLiteral(string Value) : PromNode;

// --- Selektoren -----------------------------------------------------------
/// <summary>Label-Matcher im AST (Operator + Name + Wert).</summary>
internal sealed record Matcher(string Name, string Value, MatchOp Op);

/// <summary>Match-Operator (PromQL =, !=, =~, !~).</summary>
internal enum MatchOp { Eq, Ne, Re, Nre }

/// <summary>
/// Instant-Vektor-Selektor: <c>metric_name{a="b",c=~"d.*"}</c>. Name optional
/// (leer = alle Metriken, nur Label-Filter). Offset/@ am Selektor.
/// </summary>
internal sealed record VectorSelector(
    string Name,                          // "" = kein Name-Filter
    IReadOnlyList<Matcher> Matchers,
    long OffsetMs,                        // 0 = kein offset
    long? AtMs)                           // null = kein @-Modifier
    : PromNode;

/// <summary>
/// Range-Vektor-Selektor: <c>v[5m]</c> — wickelt einen VectorSelector mit
/// einer Range-Dauer (ms). Optional Subquery-Step.
/// </summary>
internal sealed record MatrixSelector(
    VectorSelector Vector,
    long RangeMs,
    long? SubqueryStepMs) : PromNode;

// --- Operatoren -----------------------------------------------------------
internal enum BinOp { Add, Sub, Mul, Div, Mod, Pow, Eq, Ne, Gtr, Lss, Gte, Lte, And, Or, Unless }

/// <summary>Vektor-Matching-Klausel (on/ignoring, group_left/right).</summary>
internal sealed record VectorMatch(
    bool On,                              // true = on(...), false = ignoring(...)
    IReadOnlyList<string> Labels,
    string? GroupSide,                    // "left" | "right" | null
    IReadOnlyList<string>? GroupLabels);

/// <summary>
/// Binaerer Operator: <c>a + b</c>, <c>a &gt; bool b</c>, <c>a and b</c>.
/// Bool nur bei Vergleichen relevant.
/// </summary>
internal sealed record BinaryExpr(
    BinOp Op,
    PromNode Lhs,
    PromNode Rhs,
    bool Bool,
    VectorMatch? Match) : PromNode;

/// <summary>Unaerer Operator (+x / -x).</summary>
internal sealed record UnaryExpr(BinOp Op, PromNode Operand) : PromNode;

// --- Funktionen & Aggregation --------------------------------------------
internal sealed record FunctionCall(string Name, IReadOnlyList<PromNode> Args) : PromNode;

internal enum AggrModifier { By, Without, None }

/// <summary>
/// Aggregation: <c>sum by (job)(expr)</c>, <c>topk(5, expr) without (env)</c>.
/// Args: bei sum/avg/… ein Expr; bei topk/bottomk/quantile zwei (K, Expr).
/// </summary>
internal sealed record AggregateExpr(
    string Name,
    IReadOnlyList<PromNode> Args,
    AggrModifier Modifier,
    IReadOnlyList<string> Labels) : PromNode;

/// <summary>Klammerung.</summary>
internal sealed record ParenExpr(PromNode Inner) : PromNode;

// --- Fehler ---------------------------------------------------------------
/// <summary>Ausnahme bei PromQL-Syntaxfehlern (wird als errorType „bad_data“ gemappt).</summary>
public sealed class PromQLParseException : System.Exception
{
    /// <summary>0-basierte Position des Fehlers im Eingabestring.</summary>
    public int Position { get; }
    /// <summary>Erzeugt die Ausnahme mit Position und Meldung.</summary>
    public PromQLParseException(int position, string message) : base(message) { Position = position; }
}