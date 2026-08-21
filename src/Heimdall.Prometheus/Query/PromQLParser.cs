using System;
using System.Collections.Generic;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// PromQL-Parser (rekursiver Abstieg) -> PromNode-AST. Volle Grammatik:
//   expr := orExpr
//   orExpr   := andExpr ( OR andExpr )*
//   andExpr  := addExpr  ( (AND|UNLESS) addExpr )*        // and/unless gleiches Level
//   cmpExpr  := addExpr ( compOp [BOOL] [vecMatch] addExpr )?
//   addExpr  := mulExpr ( (+|-) [vecMatch] mulExpr )*
//   mulExpr  := powExpr ( (*|/|%) [vecMatch] powExpr )*
//   powExpr  := unary ( ^ [vecMatch] unary )*             // rechts-assoz
//   unary    := (+|-)? primary
//   primary  := number | string | funcCall | aggr | vectorSel | matrixSel | paren
// Vektor-Matching (on/ignoring, group_left/right) folgt dem Operator.
// ---------------------------------------------------------------------------

/// <summary>Handgeschriebener PromQL-Parser (rekursiver Abstieg, siehe Dateikommentar).</summary>
internal sealed class Parser
{
    private readonly Tok[] _t;
    private int _p;

    private Parser(IReadOnlyList<Tok> toks)
    {
        // EOF-Token garantiert; Lexer haengt es an.
        _t = new Tok[toks.Count];
        for (int i = 0; i < toks.Count; i++) _t[i] = toks[i];
    }

    /// <summary>Parst <paramref name="input"/> in einen AST; wirft <see cref="PromQLParseException"/> bei Syntaxfehlern.</summary>
    public static PromNode Parse(string input) => new Parser(Lexer.Tokenize(input)).Expr();

    // --- Token-Helfer ------------------------------------------------------
    private Tok Peek => _t[_p];
    private Tok Next() => _t[_p++];
    private bool Accept(TokKind k) { if (_t[_p].Kind == k) { _p++; return true; } return false; }
    private Tok Expect(TokKind k, string what)
    {
        if (_t[_p].Kind != k) throw new PromQLParseException(_t[_p].Pos, "expected " + what + " but got '" + _t[_p].Text + "'");
        return _t[_p++];
    }

    // --- Einstieg ----------------------------------------------------------
    private PromNode Expr() { var n = OrExpr(); if (Peek.Kind != TokKind.EOF) throw new PromQLParseException(Peek.Pos, "unexpected trailing '" + Peek.Text + "'"); return n; }

    private PromNode OrExpr()
    {
        var lhs = AndExpr();
        while (Peek.Kind == TokKind.Or) { Next(); var rhs = AndExpr(); lhs = new BinaryExpr(BinOp.Or, lhs, rhs, false, null); }
        return lhs;
    }

    private PromNode AndExpr()
    {
        var lhs = CmpExpr();
        while (Peek.Kind == TokKind.And || Peek.Kind == TokKind.Unless)
        {
            var op = Peek.Kind == TokKind.And ? BinOp.And : BinOp.Unless;
            Next();
            var rhs = CmpExpr();
            lhs = new BinaryExpr(op, lhs, rhs, false, null);
        }
        return lhs;
    }

    private PromNode CmpExpr()
    {
        var lhs = AddExpr();
        if (IsCmpOp(Peek.Kind))
        {
            var op = ToCmpOp(Peek.Kind);
            Next();
            bool b = Accept(TokKind.Bool);
            var vm = VectorMatchClause();
            var rhs = AddExpr();
            lhs = new BinaryExpr(op, lhs, rhs, b, vm);
        }
        return lhs;
    }

    private PromNode AddExpr()
    {
        var lhs = MulExpr();
        while (Peek.Kind == TokKind.Add || Peek.Kind == TokKind.Sub)
        {
            var op = Peek.Kind == TokKind.Add ? BinOp.Add : BinOp.Sub;
            Next();
            var vm = VectorMatchClause();
            var rhs = MulExpr();
            lhs = new BinaryExpr(op, lhs, rhs, false, vm);
        }
        return lhs;
    }

    private PromNode MulExpr()
    {
        var lhs = PowExpr();
        while (Peek.Kind == TokKind.Mul || Peek.Kind == TokKind.Div || Peek.Kind == TokKind.Mod)
        {
            var op = Peek.Kind switch { TokKind.Mul => BinOp.Mul, TokKind.Div => BinOp.Div, _ => BinOp.Mod };
            Next();
            var vm = VectorMatchClause();
            var rhs = PowExpr();
            lhs = new BinaryExpr(op, lhs, rhs, false, vm);
        }
        return lhs;
    }

    private PromNode PowExpr()
    {
        var lhs = Unary();
        if (Peek.Kind == TokKind.Pow)
        {
            Next();
            var vm = VectorMatchClause();
            var rhs = PowExpr(); // rechts-assoz
            return new BinaryExpr(BinOp.Pow, lhs, rhs, false, vm);
        }
        return lhs;
    }

    private PromNode Unary()
    {
        if (Peek.Kind == TokKind.Add || Peek.Kind == TokKind.Sub)
        {
            var op = Peek.Kind == TokKind.Add ? BinOp.Add : BinOp.Sub;
            Next();
            return new UnaryExpr(op, Unary());
        }
        return Primary();
    }

    // --- primary -----------------------------------------------------------
    private PromNode Primary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case TokKind.Number: Next(); return new NumberLiteral(t.Num);
            case TokKind.String: Next(); return new StringLiteral(t.Text);
            case TokKind.LParen: Next(); var inner = OrExpr(); Expect(TokKind.RParen, "')'"); return new ParenExpr(inner);
            case TokKind.LBrace: return VectorOrMatrixSelector(string.Empty);
            case TokKind.Ident:
                if (IsAggregation(t.Text)) return Aggregation();
                if (_p + 1 < _t.Length && _t[_p + 1].Kind == TokKind.LParen) return FunctionCallExpr();
                return VectorOrMatrixSelector(t.Text);
            default:
                throw new PromQLParseException(t.Pos, "unexpected token '" + t.Text + "'");
        }
    }

    private PromNode FunctionCallExpr()
    {
        var name = Next().Text;
        Expect(TokKind.LParen, "'('");
        var args = new List<PromNode>();
        if (Peek.Kind != TokKind.RParen)
        {
            args.Add(OrExpr());
            while (Accept(TokKind.Comma)) args.Add(OrExpr());
        }
        Expect(TokKind.RParen, "')'");
        return new FunctionCall(name, args);
    }

    private PromNode Aggregation()
    {
        var name = Next().Text;
        AggrModifier mod = AggrModifier.None;
        var labels = Array.Empty<string>();
        if (Peek.Kind == TokKind.By || Peek.Kind == TokKind.Without)
        {
            mod = Peek.Kind == TokKind.By ? AggrModifier.By : AggrModifier.Without;
            Next();
            labels = LabelList();
        }
        Expect(TokKind.LParen, "'('");
        var args = new List<PromNode>();
        if (Peek.Kind != TokKind.RParen)
        {
            args.Add(OrExpr());
            while (Accept(TokKind.Comma)) args.Add(OrExpr());
        }
        Expect(TokKind.RParen, "')'");
        // Trailing by/without (sum(x) by (y))
        if (Peek.Kind == TokKind.By || Peek.Kind == TokKind.Without)
        {
            mod = Peek.Kind == TokKind.By ? AggrModifier.By : AggrModifier.Without;
            Next();
            labels = LabelList();
        }
        return new AggregateExpr(name, args, mod, labels);
    }

    private string[] LabelList()
    {
        Expect(TokKind.LParen, "'('");
        var list = new List<string>();
        if (Peek.Kind != TokKind.RParen)
        {
            list.Add(IdentName());
            while (Accept(TokKind.Comma)) list.Add(IdentName());
        }
        Expect(TokKind.RParen, "')'");
        return list.ToArray();
    }

    private PromNode VectorOrMatrixSelector(string name)
    {
        if (Peek.Kind == TokKind.Ident) Next(); // Name konsumieren (außer Aufrufer übergab "" und current ist LBrace)
        var matchers = Array.Empty<Matcher>();
        if (Peek.Kind == TokKind.LBrace) matchers = Matchers();

        long offsetMs = 0;
        if (Peek.Kind == TokKind.Offset) { Next(); offsetMs = ExpectDuration("offset"); }

        long? atMs = null;
        if (Peek.Kind == TokKind.At)
        {
            Next();
            // @ akzeptiert eine Zahl (Unix-Sekunden) oder start()/end() — hier als
            // NumberLiteral/FunctionCall geparst und zur Laufzeit zu ms aufgeloest.
            var atNode = Unary();
            atMs = atNode is NumberLiteral nl ? (long)(nl.Value * 1000) : -1; // -1 = „zur Laufzeit evaluieren“ (Phase 2/3)
        }

        var vs = new VectorSelector(name, matchers, offsetMs, atMs);

        if (Peek.Kind == TokKind.LBracket)
        {
            Next();
            long range = ExpectDuration("range");
            long? step = null;
            if (Accept(TokKind.Colon)) step = ExpectDuration("subquery step");
            if (Accept(TokKind.Colon)) ExpectDuration("subquery resolution"); // 3. Komponente ignoriert (== step)
            Expect(TokKind.RBracket, "']'");
            return new MatrixSelector(vs, range, step);
        }
        return vs;
    }

    private Matcher[] Matchers()
    {
        Expect(TokKind.LBrace, "'{'");
        var list = new List<Matcher>();
        if (Peek.Kind != TokKind.RBrace)
        {
            list.Add(Matcher());
            while (Accept(TokKind.Comma)) list.Add(Matcher());
        }
        Expect(TokKind.RBrace, "'}'");
        return list.ToArray();
    }

    private Matcher Matcher()
    {
        var name = IdentName();
        MatchOp op;
        switch (Peek.Kind)
        {
            case TokKind.Assign: op = MatchOp.Eq; break;
            case TokKind.Ne: op = MatchOp.Ne; break;
            case TokKind.Match: op = MatchOp.Re; break;
            case TokKind.NMatch: op = MatchOp.Nre; break;
            default: throw new PromQLParseException(Peek.Pos, "expected matcher operator (=,!=,=~,!~)");
        }
        Next();
        var val = Expect(TokKind.String, "string value");
        return new Matcher(name, val.Text, op);
    }

    // --- Vektor-Matching nach Operator -------------------------------------
    private VectorMatch? VectorMatchClause()
    {
        if (Peek.Kind != TokKind.On && Peek.Kind != TokKind.Ignoring) return null;
        bool on = Peek.Kind == TokKind.On;
        Next();
        var labels = LabelList();
        string? groupSide = null;
        string[]? groupLabels = null;
        if (Peek.Kind == TokKind.GroupLeft || Peek.Kind == TokKind.GroupRight)
        {
            groupSide = Peek.Kind == TokKind.GroupLeft ? "left" : "right";
            Next();
            if (Peek.Kind == TokKind.LParen) groupLabels = LabelList();
        }
        return new VectorMatch(on, labels, groupSide, groupLabels);
    }

    // --- Kleine Helfer -----------------------------------------------------
    private long ExpectDuration(string what)
    {
        if (Peek.Kind != TokKind.Duration) throw new PromQLParseException(Peek.Pos, "expected duration for " + what);
        return Next().DurMs;
    }

    private string IdentName()
    {
        if (Peek.Kind != TokKind.Ident) throw new PromQLParseException(Peek.Pos, "expected identifier but got '" + Peek.Text + "'");
        return Next().Text;
    }

    private static bool IsCmpOp(TokKind k) => k == TokKind.Eq || k == TokKind.Ne || k == TokKind.Gtr || k == TokKind.Lss || k == TokKind.Gte || k == TokKind.Lte;
    private static BinOp ToCmpOp(TokKind k) => k switch
    {
        TokKind.Eq => BinOp.Eq, TokKind.Ne => BinOp.Ne, TokKind.Gtr => BinOp.Gtr,
        TokKind.Lss => BinOp.Lss, TokKind.Gte => BinOp.Gte, TokKind.Lte => BinOp.Lte, _ => BinOp.Eq
    };

    private static readonly System.Collections.Generic.HashSet<string> _aggregations = new(System.StringComparer.Ordinal)
    { "sum", "avg", "max", "min", "count", "group", "stddev", "stdvar", "count_values", "topk", "bottomk", "quantile" };

    private static bool IsAggregation(string name) => _aggregations.Contains(name);
}