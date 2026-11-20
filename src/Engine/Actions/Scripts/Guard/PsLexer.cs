using System.Text;

namespace OptimizationTool.Engine.Actions.Scripts.Guard;

internal enum TokKind { Word, String, Variable, Splat, Parameter, Op, TypeLit, Member, NewLine }

/// <summary>
/// One token of the exact script text. <see cref="Value"/> is what PowerShell would see: a word with its backtick escapes
/// resolved, a string's content, a variable's name, a parameter's name with its dash, a type literal's inner text.
/// </summary>
internal sealed record Tok(TokKind Kind, string Text, string Value, int Line, bool Space, bool Expandable = false, bool Static = false, bool Invoked = false);

/// <summary>
/// A small PowerShell-aware tokenizer for the guard's first layer (contracts/m9-ai-powers.md §5.3). It knows line comments,
/// single- and double-quoted strings (with backtick escapes and the code inside "$( … )"), variables, splatting, parameters,
/// type literals, member access, statement separators, pipelines and line continuation. Constructs that hide content from
/// a reader are not tokenized but refused outright (<see cref="Refusals"/>): here-strings and block comments, plus anything
/// it cannot read to its end (an unterminated string or bracket). It never executes anything and is not a full parser:
/// PowerShell's own parser is the second layer.
/// </summary>
internal sealed class PsLexer
{
    private readonly string _s;
    private int _i;
    private int _line = 1;
    private bool _space = true;
    private List<Tok> _out = [];

    /// <summary>(line, why) for each construct refused before analysis (dynamic_code).</summary>
    public List<(int Line, string Why)> Refusals { get; } = [];

    public IReadOnlyList<Tok> Tokens => _out;

    private PsLexer(string s) => _s = s;

    public static PsLexer Run(string text)
    {
        var lexer = new PsLexer(text);
        lexer.Lex(closeOnParen: false);
        return lexer;
    }

    private char At(int k) => k < _s.Length ? _s[k] : '\0';
    private char Cur => At(_i);
    private char Next => At(_i + 1);

    private void Add(TokKind kind, string text, string value, int line, bool expandable = false, bool isStatic = false, bool invoked = false)
    {
        _out.Add(new Tok(kind, text, value, line, _space, expandable, isStatic, invoked));
        _space = false;
    }

    private Tok? Last => _out.Count == 0 ? null : _out[^1];

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
    private static bool WordStop(char c) => c is ' ' or '\t' or '\r' or '\n' or ';' or '|' or '&' or '(' or ')' or '{' or '}' or ',' or '>' or '<' or '\0';

    /// <summary>Lexes to the end of the text, or (inside "$( … )" of a string) to the ')' that closes it. Returns false when that ')' never came.</summary>
    private bool Lex(bool closeOnParen)
    {
        var depth = 0;
        while (_i < _s.Length)
        {
            var c = Cur;
            switch (c)
            {
                case '\r':
                    _i++;
                    continue;
                case '\n':
                    Add(TokKind.NewLine, "\n", "\n", _line);
                    _line++;
                    _i++;
                    _space = true;
                    continue;
                case ' ' or '\t':
                    _space = true;
                    _i++;
                    continue;
                case '`' when Next is '\r' or '\n':
                    // Line continuation: the statement goes on on the next line.
                    _i++;
                    if (Cur == '\r') _i++;
                    if (Cur == '\n') { _i++; _line++; }
                    _space = true;
                    continue;
                case '#':
                    while (_i < _s.Length && Cur != '\n') _i++;
                    continue;
                case '<' when Next == '#':
                    {
                        // A block comment can hold text that looks like code (or hide code behind text): refused.
                        Refusals.Add((_line, "block_comment"));
                        _i += 2;
                        while (_i < _s.Length && !(Cur == '#' && Next == '>')) { if (Cur == '\n') _line++; _i++; }
                        _i = Math.Min(_s.Length, _i + 2);
                        continue;
                    }
                case '@' when (Next is '"' or '\'') && RestOfLineBlank(_i + 2):
                    {
                        // A here-string carries a block of text that a later step could run: refused, skipped to its end.
                        Refusals.Add((_line, "here_string"));
                        var quote = Next;
                        _i += 2;
                        var closed = false;
                        while (_i < _s.Length)
                        {
                            if (Cur == '\n')
                            {
                                _line++;
                                _i++;
                                if (Cur == quote && Next == '@') { _i += 2; closed = true; break; }
                                continue;
                            }
                            _i++;
                        }
                        if (!closed) Refusals.Add((_line, "unterminated"));
                        continue;
                    }
                case '\'':
                    ReadSingle();
                    continue;
                case '"':
                    ReadDouble();
                    continue;
                case '$':
                    if (Next == '(')
                    {
                        Add(TokKind.Op, "$(", "$(", _line);
                        _i += 2;
                        depth++;
                    }
                    else ReadVariable();
                    continue;
                case '@':
                    if (Next == '(') { Add(TokKind.Op, "@(", "@(", _line); _i += 2; depth++; }
                    else if (Next == '{') { Add(TokKind.Op, "@{", "@{", _line); _i += 2; }
                    else if (char.IsAsciiLetterOrDigit(Next) || Next is '_' or '?')
                    {
                        var start = _i++;
                        while (char.IsAsciiLetterOrDigit(Cur) || Cur is '_' or ':' or '?') _i++;
                        Add(TokKind.Splat, _s[start.._i], _s[(start + 1).._i], _line);
                    }
                    else { Add(TokKind.Op, "@", "@", _line); _i++; }
                    continue;
                case '[':
                    if (Last is { } prev && !_space && prev.Kind is TokKind.Variable or TokKind.TypeLit or TokKind.String or TokKind.Member
                        || (Last is { Kind: TokKind.Op, Value: ")" or "]" } && !_space))
                    {
                        Add(TokKind.Op, "[", "[", _line);
                        _i++;
                    }
                    else ReadTypeLiteral();
                    continue;
                case ']':
                    Add(TokKind.Op, "]", "]", _line);
                    _i++;
                    continue;
                case ':' when Next == ':':
                    Add(TokKind.Op, "::", "::", _line);
                    _i += 2;
                    ReadMember(isStatic: true);
                    continue;
                case '.':
                    if (Next == '.') { Add(TokKind.Op, "..", "..", _line); _i += 2; continue; }
                    if (!_space && Last is { } p && (p.Kind is TokKind.Variable or TokKind.String or TokKind.Member or TokKind.TypeLit
                        || p is { Kind: TokKind.Op, Value: ")" or "]" }))
                    {
                        _i++;
                        ReadMember(isStatic: false);
                        continue;
                    }
                    if (Next is ' ' or '\t' or '$' or '(' or '"' or '\'' or '{' or '&' or '@')
                    {
                        Add(TokKind.Op, ".", ".", _line); // dot-source operator
                        _i++;
                        continue;
                    }
                    ReadWord();
                    continue;
                case '-':
                    if (char.IsAsciiLetter(Next) || Next == '_')
                    {
                        var start = _i++;
                        while (IsNameChar(Cur) || Cur == '?') _i++;
                        var name = _s[start.._i];
                        Add(TokKind.Parameter, name, name, _line);
                        if (Cur == ':' && Next != ':') { _i++; _space = false; } // -Name:value — the value follows as its own token
                        continue;
                    }
                    if (char.IsAsciiDigit(Next) || Next == '.') { ReadWord(); continue; }
                    if (Next is '-' or '=') { Add(TokKind.Op, _s.Substring(_i, 2), _s.Substring(_i, 2), _line); _i += 2; continue; }
                    Add(TokKind.Op, "-", "-", _line);
                    _i++;
                    continue;
                case '(' or '{':
                    Add(TokKind.Op, c.ToString(), c.ToString(), _line);
                    if (c == '(') depth++;
                    _i++;
                    continue;
                case ')':
                    if (closeOnParen && depth == 0) { _i++; return true; }
                    if (depth > 0) depth--;
                    Add(TokKind.Op, ")", ")", _line);
                    _i++;
                    continue;
                case '}':
                    Add(TokKind.Op, "}", "}", _line);
                    _i++;
                    continue;
                case '/' or '*' when char.IsAsciiLetterOrDigit(Next) || Next is '?' or '.' or '\\' or '*':
                    ReadWord(); // a switch (/c, /set) or a wildcard (*.tmp): part of an argument
                    continue;
                case '|' or '&' or ';' or ',' or '=' or '!' or '+' or '*' or '/' or '%' or '<' or '>':
                    ReadOperator();
                    continue;
                default:
                    if (char.IsAsciiDigit(c) && Next == '>') { ReadOperator(); continue; }
                    ReadWord();
                    continue;
            }
        }
        if (closeOnParen) return false;
        return true;
    }

    private bool RestOfLineBlank(int k)
    {
        while (k < _s.Length && _s[k] is ' ' or '\t') k++;
        return k >= _s.Length || _s[k] is '\r' or '\n';
    }

    private void ReadOperator()
    {
        var start = _i;
        var c = Cur;
        string op;
        if ((char.IsAsciiDigit(c) || c == '*') && Next == '>')
        {
            // Redirections: 2> 2>> 2>&1 *> …
            _i++;
            _i++;
            if (Cur == '>') _i++;
            if (Cur == '&' && char.IsAsciiDigit(Next)) _i += 2;
            Add(TokKind.Op, _s[start.._i], ">", _line);
            return;
        }
        var two = _i + 1 < _s.Length ? _s.Substring(_i, 2) : "";
        if (two is "||" or "&&" or "+=" or "*=" or "/=" or "%=" or "==" or ">>")
            op = two;
        else if (c == '>' && Next == '&' && char.IsAsciiDigit(At(_i + 2))) op = _s.Substring(_i, 3);
        else op = c.ToString();
        _i += op.Length;
        Add(TokKind.Op, op, op is ">" or ">>" || op.StartsWith(">&", StringComparison.Ordinal) ? ">" : op, _line);
    }

    /// <summary>A bare word (argument-mode token): backtick escapes resolved, embedded quotes read as part of it.</summary>
    private void ReadWord()
    {
        var start = _i;
        var line = _line;
        var value = new StringBuilder();
        var expandable = false;
        while (_i < _s.Length && !WordStop(Cur))
        {
            var c = Cur;
            if (c == '`')
            {
                if (Next is '\r' or '\n' or '\0') break;
                value.Append(Next);
                _i += 2;
                continue;
            }
            if (c == '\'')
            {
                _i++;
                while (_i < _s.Length && !(Cur == '\'' && Next != '\''))
                {
                    if (Cur == '\'' && Next == '\'') { value.Append('\''); _i += 2; continue; }
                    if (Cur == '\n') _line++;
                    value.Append(Cur);
                    _i++;
                }
                if (_i >= _s.Length) Refusals.Add((line, "unterminated"));
                _i++;
                continue;
            }
            if (c == '"')
            {
                _i++;
                while (_i < _s.Length && Cur != '"')
                {
                    if (Cur == '`' && _i + 1 < _s.Length) { value.Append(Next); _i += 2; continue; }
                    if (Cur == '$') expandable = true;
                    if (Cur == '\n') _line++;
                    value.Append(Cur);
                    _i++;
                }
                if (_i >= _s.Length) Refusals.Add((line, "unterminated"));
                _i++;
                continue;
            }
            if (c == '$') expandable = true;
            value.Append(c);
            _i++;
        }
        if (_i == start) { _i++; return; } // a lone character the switch did not take: skip it
        Add(TokKind.Word, _s[start.._i], value.ToString(), line, expandable);
    }

    private void ReadSingle()
    {
        var line = _line;
        var start = _i++;
        var value = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) { Refusals.Add((line, "unterminated")); break; }
            var c = Cur;
            if (c == '\'')
            {
                if (Next == '\'') { value.Append('\''); _i += 2; continue; }
                _i++;
                break;
            }
            if (c == '\n') _line++;
            value.Append(c);
            _i++;
        }
        Add(TokKind.String, _s[start..Math.Min(_i, _s.Length)], value.ToString(), line);
    }

    /// <summary>A double-quoted string. Code inside "$( … )" is lexed as tokens that follow the string, so the analysis sees it.</summary>
    private void ReadDouble()
    {
        var line = _line;
        var start = _i++;
        var value = new StringBuilder();
        var expandable = false;
        List<Tok>? inner = null;
        while (true)
        {
            if (_i >= _s.Length) { Refusals.Add((line, "unterminated")); break; }
            var c = Cur;
            if (c == '"')
            {
                if (Next == '"') { value.Append('"'); _i += 2; continue; }
                _i++;
                break;
            }
            if (c == '`' && _i + 1 < _s.Length)
            {
                var e = Next;
                value.Append(e switch { '0' => '\0', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => e });
                if (e == '\n') _line++;
                _i += 2;
                continue;
            }
            if (c == '$' && Next == '(')
            {
                expandable = true;
                var saved = _out;
                var savedSpace = _space;
                _out = [];
                _space = true;
                Add(TokKind.Op, "$(", "$(", _line);
                _i += 2;
                var closed = Lex(closeOnParen: true);
                Add(TokKind.Op, ")", ")", _line);
                (inner ??= []).AddRange(_out);
                _out = saved;
                _space = savedSpace;
                value.Append("$()");
                if (!closed) { Refusals.Add((line, "unterminated")); break; }
                continue;
            }
            if (c == '$' && (char.IsAsciiLetter(Next) || Next is '_' or '{' or '?' or '$')) expandable = true;
            if (c == '\n') _line++;
            value.Append(c);
            _i++;
        }
        Add(TokKind.String, _s[start..Math.Min(_i, _s.Length)], value.ToString(), line, expandable);
        if (inner is not null) foreach (var t in inner) _out.Add(t);
    }

    private void ReadVariable()
    {
        var start = _i++;
        string name;
        if (Cur == '{')
        {
            _i++;
            var sb = new StringBuilder();
            while (_i < _s.Length && Cur != '}')
            {
                if (Cur == '`' && _i + 1 < _s.Length) { sb.Append(Next); _i += 2; continue; }
                if (Cur == '\n') _line++;
                sb.Append(Cur);
                _i++;
            }
            if (_i >= _s.Length) Refusals.Add((_line, "unterminated"));
            _i++;
            name = sb.ToString();
        }
        else if (Cur is '$' or '?' or '^' or '_' && !char.IsAsciiLetterOrDigit(Next))
        {
            name = Cur.ToString();
            _i++;
        }
        else
        {
            var n = _i;
            while (char.IsAsciiLetterOrDigit(Cur) || Cur is '_' or '?' || (Cur == ':' && Next != ':' && Next != '\\' && _i > n)) _i++;
            name = _s[n.._i];
            if (name.Length == 0) { Add(TokKind.Word, "$", "$", _line); return; }
        }
        Add(TokKind.Variable, _s[start..Math.Min(_i, _s.Length)], name, _line);
    }

    /// <summary>[Type] or [Attribute(…)]: the balanced bracket text; its value is the inner text.</summary>
    private void ReadTypeLiteral()
    {
        var start = _i;
        var depth = 0;
        var k = _i;
        while (k < _s.Length && _s[k] is not ('\r' or '\n'))
        {
            if (_s[k] == '[') depth++;
            else if (_s[k] == ']' && --depth == 0) break;
            k++;
        }
        if (depth != 0 || k >= _s.Length || _s[k] != ']')
        {
            Add(TokKind.Op, "[", "[", _line);
            _i++;
            return;
        }
        _i = k + 1;
        var inner = _s[(start + 1)..k].Trim();
        Add(TokKind.TypeLit, _s[start.._i], inner, _line);
    }

    /// <summary>A member after '.' or '::': its name, or "" when the name is not a constant ($x.$name, .(expr), ."str").</summary>
    private void ReadMember(bool isStatic)
    {
        var start = _i;
        string name;
        if (char.IsAsciiLetter(Cur) || Cur == '_')
        {
            while (char.IsAsciiLetterOrDigit(Cur) || Cur == '_') _i++;
            name = _s[start.._i];
        }
        else if (Cur is '\'' or '"')
        {
            // A quoted member name is a string in disguise: treated as not a constant.
            var q = Cur;
            _i++;
            while (_i < _s.Length && Cur != q && Cur != '\n') _i++;
            if (_i < _s.Length && Cur == q) _i++;
            name = "";
        }
        else name = ""; // $x.$name or .(expr): the variable or bracket is lexed next as its own token
        var invoked = Cur == '(' || (Cur == '{' && name.Length > 0);
        _space = false;
        _out.Add(new Tok(TokKind.Member, _s[Math.Max(0, start - (isStatic ? 2 : 1))..Math.Max(_i, start)], name, _line, false, false, isStatic, invoked));
    }
}
