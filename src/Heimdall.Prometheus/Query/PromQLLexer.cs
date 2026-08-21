using System;
using System.Collections.Generic;
using System.Globalization;

namespace Heimdall.Prometheus;

// ---------------------------------------------------------------------------
// PromQL-Lexer (handgeschrieben). Token-Arten: Zahlen, Durations (5m, 1h30m),
// Strings ('' "" ``), Identifier (Metrik-/Funktions-/Schlüsselwort-Namen),
// Operatoren und Interpunktion. Durations sind Integer+direkt folgendem
// Unit-Buchstaben (s/m/h/d/w/y); sonst Zahl. Keine Regex-/Antlr-Abhängigkeit.
// ---------------------------------------------------------------------------

internal enum TokKind
{
    EOF, Number, Duration, String, Ident,
    Add, Sub, Mul, Div, Mod, Pow,
    Assign, Eq, Ne, Gtr, Lss, Gte, Lte, Match, NMatch,
    And, Or, Unless, Bool, Offset,
    By, Without, On, Ignoring, GroupLeft, GroupRight,
    LParen, RParen, LBrace, RBrace, LBracket, RBracket, Comma, Colon, At
}

internal readonly struct Tok
{
    public readonly TokKind Kind;
    public readonly string Text;     // Ident/String-Rohwert bzw.原文 für Zahlen
    public readonly double Num;      // für Number
    public readonly long DurMs;      // für Duration
    /// <summary>Position (0-basiert) im Eingabestring.</summary>
    public readonly int Pos;
    /// <summary>Erzeugt ein Token.</summary>
    public Tok(TokKind kind, string text, double num, long dur, int pos)
    { Kind = kind; Text = text; Num = num; DurMs = dur; Pos = pos; }
}

/// <summary>Handgeschriebener PromQL-Lexer (siehe Dateikommentar).</summary>
internal sealed class Lexer
{
    private readonly string _s;
    private int _i;
    private readonly List<Tok> _toks = new();

    private Lexer(string s) { _s = s; }

    /// <summary>Tokenisiert <paramref name="input"/>; wirft <see cref="PromQLParseException"/> bei ungueltigen Zeichen.</summary>
    public static IReadOnlyList<Tok> Tokenize(string input)
    {
        var lx = new Lexer(input ?? string.Empty);
        lx.Run();
        return lx._toks;
    }

    /// <summary>Parst eine Prom-Dauer ("5m", "1h30m", "2h") → ms; null bei Ungültig.</summary>
    public static long? TryParseDurationMs(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        int i = 0;
        long ms = 0;
        bool any = false;
        while (i < s.Length)
        {
            int dStart = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == dStart) return null;           // Unit ohne Zahl
            if (i >= s.Length || !IsDurUnit(s[i])) return null;
            long n = long.Parse(s.AsSpan(dStart, i - dStart), CultureInfo.InvariantCulture);
            ms += n * UnitMs(s[i]);
            i++;
            any = true;
        }
        return any ? ms : null;
    }

    private void Run()
    {
        while (_i < _s.Length)
        {
            char c = _s[_i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { _i++; continue; }
            int start = _i;

            if (char.IsDigit(c)) { NumberOrDuration(start); continue; }
            if (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])) { NumberOrDuration(start); continue; }

            if (c == '"' || c == '\'' || c == '`') { String(c, start); continue; }

            if (IsIdentStart(c)) { Ident(start); continue; }

            switch (c)
            {
                case '+': _i++; Push(TokKind.Add, start); continue;
                case '-': _i++; Push(TokKind.Sub, start); continue;
                case '*': _i++; Push(TokKind.Mul, start); continue;
                case '/': _i++; Push(TokKind.Div, start); continue;
                case '%': _i++; Push(TokKind.Mod, start); continue;
                case '^': _i++; Push(TokKind.Pow, start); continue;
                case '(': _i++; Push(TokKind.LParen, start); continue;
                case ')': _i++; Push(TokKind.RParen, start); continue;
                case '{': _i++; Push(TokKind.LBrace, start); continue;
                case '}': _i++; Push(TokKind.RBrace, start); continue;
                case '[': _i++; Push(TokKind.LBracket, start); continue;
                case ']': _i++; Push(TokKind.RBracket, start); continue;
                case ',': _i++; Push(TokKind.Comma, start); continue;
                case ':': _i++; Push(TokKind.Colon, start); continue;
                case '@': _i++; Push(TokKind.At, start); continue;
                case '=':
                    if (_i + 1 < _s.Length && _s[_i + 1] == '~') { _i += 2; Push(TokKind.Match, start); }
                    else if (_i + 1 < _s.Length && _s[_i + 1] == '=') { _i += 2; Push(TokKind.Eq, start); }
                    else { _i++; Push(TokKind.Assign, start); }
                    continue;
                case '!':
                    if (_i + 1 < _s.Length && _s[_i + 1] == '=') { _i += 2; Push(TokKind.Ne, start); }
                    else if (_i + 1 < _s.Length && _s[_i + 1] == '~') { _i += 2; Push(TokKind.NMatch, start); }
                    else throw new PromQLParseException(_i, "unexpected '!'");
                    continue;
                case '>':
                    if (_i + 1 < _s.Length && _s[_i + 1] == '=') { _i += 2; Push(TokKind.Gte, start); }
                    else { _i++; Push(TokKind.Gtr, start); }
                    continue;
                case '<':
                    if (_i + 1 < _s.Length && _s[_i + 1] == '=') { _i += 2; Push(TokKind.Lte, start); }
                    else { _i++; Push(TokKind.Lss, start); }
                    continue;
                default:
                    throw new PromQLParseException(_i, "unexpected character '" + c + "'");
            }
        }
        _toks.Add(new Tok(TokKind.EOF, string.Empty, 0, 0, _i));
    }

    private void Push(TokKind kind, int pos) => _toks.Add(new Tok(kind, _s.Substring(pos, _i - pos), 0, 0, pos));

    private void NumberOrDuration(int start)
    {
        // Ziffern-Teil konsumieren (inkl. . und Exponent) — aber erst prüfen, ob
        // direkt nach der ersten Zifferfolge ein Duration-Unit kommt (ohne ./e).
        int j = _i;
        while (j < _s.Length && char.IsDigit(_s[j])) j++;
        bool isDuration = j < _s.Length && IsDurUnit(_s[j]);
        if (isDuration)
        {
            long ms = 0;
            while (_i < _s.Length)
            {
                int dStart = _i;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                if (_i == dStart) break; // Unit ohne Zahl → Ende
                if (_i >= _s.Length || !IsDurUnit(_s[_i])) break;
                long n = long.Parse(_s.Substring(dStart, _i - dStart), CultureInfo.InvariantCulture);
                ms += n * UnitMs(_s[_i]);
                _i++;
            }
            _toks.Add(new Tok(TokKind.Duration, _s.Substring(start, _i - start), 0, ms, start));
            return;
        }
        // Zahl: digits (. digits)? (e[+-]?digits)?
        _i = j;
        if (_i < _s.Length && _s[_i] == '.')
        {
            _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
        }
        if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
        {
            _i++;
            if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
        }
        // Inf/NaN werden in Prometheus als Identifier behandelt — hier als Zahl? Prom
        // kennt `inf`/`Inf`/`nan`. Wir lassen sie als Ident laufen (siehe Ident).
        var raw = _s.Substring(start, _i - start);
        double num = double.Parse(raw, CultureInfo.InvariantCulture);
        _toks.Add(new Tok(TokKind.Number, raw, num, 0, start));
    }

    private void String(char quote, int start)
    {
        _i++; // Öffnungs-Quote
        var sb = new System.Text.StringBuilder();
        bool raw = quote == '`';
        while (_i < _s.Length)
        {
            char c = _s[_i];
            if (c == quote)
            {
                if (!raw && _i + 1 < _s.Length && _s[_i + 1] == quote) { sb.Append(quote); _i += 2; continue; } // '' escaping
                _i++; break;
            }
            if (!raw && c == '\\')
            {
                _i++;
                if (_i >= _s.Length) break;
                char e = _s[_i];
                sb.Append(e switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '"' => '"', '\'' => '\'', '`' => '`',
                    _ => e
                });
                _i++;
            }
            else { sb.Append(c); _i++; }
        }
        _toks.Add(new Tok(TokKind.String, sb.ToString(), 0, 0, start));
    }

    private void Ident(int start)
    {
        while (_i < _s.Length && IsIdentPart(_s[_i])) _i++;
        var text = _s.Substring(start, _i - start);
        var kind = text switch
        {
            "and" => TokKind.And,
            "or" => TokKind.Or,
            "unless" => TokKind.Unless,
            "bool" => TokKind.Bool,
            "offset" => TokKind.Offset,
            "by" => TokKind.By,
            "without" => TokKind.Without,
            "on" => TokKind.On,
            "ignoring" => TokKind.Ignoring,
            "group_left" => TokKind.GroupLeft,
            "group_right" => TokKind.GroupRight,
            _ => TokKind.Ident
        };
        _toks.Add(new Tok(kind, text, 0, 0, start));
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == ':';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == ':';
    private static bool IsDurUnit(char c) => c == 's' || c == 'm' || c == 'h' || c == 'd' || c == 'w' || c == 'y';
    private static long UnitMs(char u) => u switch
    {
        's' => 1000L, 'm' => 60_000L, 'h' => 3_600_000L, 'd' => 86_400_000L, 'w' => 604_800_000L, 'y' => 31_536_000_000L, _ => 0
    };
}