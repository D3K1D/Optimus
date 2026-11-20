namespace OptimizationTool.Engine.Actions.Scripts.Guard;

/// <summary>One argument: its literal value (null when not a constant), the raw text, a parameter ("-Name") or a splat.</summary>
internal sealed record GArg(string? Literal, string Raw, int Line, bool Param = false, bool Splat = false);

/// <summary>
/// One command as the guard sees it: its name (null when not a literal, e.g. &amp; $x), the invocation operator ("", "&amp;"
/// or "."), the line and its arguments. <see cref="Block"/>: a literal script block invoked with &amp; or . (its content is
/// on the card and analysed as its own statements).
/// </summary>
internal sealed record GCmd(string? Name, string Op, int Line, List<GArg> Args, int Pipeline, bool Block = false);

internal sealed record GMember(string? Name, bool Static, string? Type, bool Invoked, int Line);

/// <summary>What the guard checks, from either layer: commands (with pipelines), members, type names, variables, redirection targets.</summary>
internal sealed class GuardInput
{
    public List<GCmd> Commands { get; } = [];
    public List<GMember> Members { get; } = [];
    public List<(string Name, int Line)> Types { get; } = [];
    public List<(string Name, int Line)> Variables { get; } = [];
    public List<GArg> Redirects { get; } = [];
    public HashSet<string> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every constant text in the script (words, strings, parameters, type and member names) with its line.</summary>
    public List<(string Text, int Line)> Literals { get; } = [];
}

/// <summary>
/// Turns <see cref="PsLexer"/> tokens into commands: a word in command position (statement start, after |, ;, =, a newline
/// or an opening bracket) is a command, the tokens after it are its arguments; "&amp;" and "." take the next token as the
/// command whatever it is (a variable, a bracket or a concatenation is a name that is not a literal). Brackets inside a
/// statement start nested statements. Keywords (if, foreach, function, …) are not commands; a word followed by '=' (a
/// hashtable key) is not a command.
/// </summary>
internal static class PsAnalyzer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elseif", "else", "switch", "foreach", "for", "while", "do", "until", "try", "catch", "finally", "function", "filter",
        "return", "break", "continue", "throw", "exit", "param", "begin", "process", "end", "trap", "data", "dynamicparam", "class",
        "enum", "using", "workflow", "parallel", "sequence", "inlinescript", "configuration", "in", "hidden", "static", "default",
    };

    /// <summary>Keywords after which a pipeline may follow directly (return Get-X, throw "x").</summary>
    private static readonly HashSet<string> PipelineKeywords = new(StringComparer.OrdinalIgnoreCase) { "return", "throw", "exit" };

    private sealed class Frame
    {
        public bool CmdPos = true;
        public GCmd? Cmd;
        public string? CallOp;
        public bool Redirect;
        public bool FunctionName;
    }

    private static bool Opens(string op) => op is "(" or "$(" or "@(" or "{" or "@{";

    public static GuardInput Run(IReadOnlyList<Tok> tokens)
    {
        var input = new GuardInput();
        var stack = new Stack<Frame>();
        stack.Push(new Frame());
        var pipeline = 0;
        Tok? prev = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var f = stack.Peek();
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;

            switch (t.Kind)
            {
                case TokKind.Word or TokKind.String or TokKind.Parameter:
                    input.Literals.Add((t.Value, t.Line));
                    break;
                case TokKind.TypeLit:
                    input.Literals.Add((t.Value, t.Line));
                    input.Types.Add((t.Value, t.Line));
                    break;
                case TokKind.Member:
                    input.Literals.Add((t.Value, t.Line));
                    var type = t.Static && prev is { Kind: TokKind.Op, Value: "::" } && i >= 2 && tokens[i - 2].Kind == TokKind.TypeLit ? tokens[i - 2].Value : null;
                    input.Members.Add(new GMember(t.Value.Length == 0 ? null : t.Value, t.Static, type, t.Invoked, t.Line));
                    break;
                case TokKind.Variable:
                    input.Variables.Add((t.Value, t.Line));
                    break;
            }

            if (t.Kind == TokKind.NewLine)
            {
                // A newline after |, =, ',' or an opening bracket continues the statement.
                if (prev is { Kind: TokKind.Op } p && (p.Value is "|" or "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "," or "&&" or "||" || Opens(p.Value)))
                    continue;
                EndStatement(f);
                pipeline++;
                prev = t;
                continue;
            }

            if (t.Kind == TokKind.Op)
            {
                switch (t.Value)
                {
                    case ";" or "&&" or "||":
                        EndStatement(f);
                        pipeline++;
                        break;
                    case "|":
                        f.Cmd = null;
                        f.CmdPos = true;
                        f.CallOp = null;
                        break;
                    case "=" or "+=" or "-=" or "*=" or "/=" or "%=":
                        f.Cmd = null;
                        f.CmdPos = true;
                        f.CallOp = null;
                        pipeline++;
                        break;
                    case "&" or "." when f.CmdPos || f.Cmd is null:
                        f.CallOp = t.Value;
                        f.CmdPos = true;
                        break;
                    case ">":
                        f.Redirect = true;
                        break;
                    case "%" when f.CmdPos && f.CallOp is null:
                        StartCommand(input, f, "ForEach-Object", "", t.Line, pipeline);
                        break;
                    case var op when Opens(op):
                        if (f.CallOp is not null)
                        {
                            // & (…) / & $(…) runs whatever the bracket yields: not a literal. & { … } runs a block that is on the card.
                            var block = op == "{";
                            StartCommand(input, f, block ? "{block}" : null, f.CallOp, t.Line, pipeline, block);
                        }
                        else if (f.Cmd is not null) f.Cmd.Args.Add(new GArg(null, op, t.Line));
                        else f.CmdPos = false; // an expression statement: (…), @(…), a hashtable
                        stack.Push(new Frame());
                        pipeline++;
                        break;
                    case ")" or "}":
                        if (stack.Count > 1) stack.Pop();
                        pipeline++;
                        break;
                    case ",":
                        break;
                    default:
                        if (f.Cmd is null) f.CmdPos = false; // an operator in an expression
                        break;
                }
                prev = t;
                continue;
            }

            if (t.Kind == TokKind.Member) { prev = t; continue; }

            if (f.Redirect)
            {
                input.Redirects.Add(Arg(t));
                f.Redirect = false;
                prev = t;
                continue;
            }

            if (f.CallOp is not null)
            {
                var name = t.Kind switch
                {
                    TokKind.Word => t.Expandable ? null : t.Value,
                    TokKind.String => t.Expandable ? null : t.Value,
                    _ => null,
                };
                StartCommand(input, f, name, f.CallOp, t.Line, pipeline);
                prev = t;
                continue;
            }

            if (f.FunctionName)
            {
                if (t.Kind == TokKind.Word) input.Functions.Add(t.Value.Contains(':') ? t.Value[(t.Value.IndexOf(':') + 1)..] : t.Value);
                f.FunctionName = false;
                f.CmdPos = false;
                prev = t;
                continue;
            }

            if (f.CmdPos)
            {
                if (t.Kind == TokKind.Word && !LooksNumeric(t.Value) && !t.Expandable)
                {
                    if (next is { Kind: TokKind.Op, Value: "=" }) { f.CmdPos = false; prev = t; continue; } // a hashtable key
                    if (Keywords.Contains(t.Value))
                    {
                        if (t.Value.Equals("function", StringComparison.OrdinalIgnoreCase) || t.Value.Equals("filter", StringComparison.OrdinalIgnoreCase)
                            || t.Value.Equals("workflow", StringComparison.OrdinalIgnoreCase) || t.Value.Equals("configuration", StringComparison.OrdinalIgnoreCase))
                            f.FunctionName = true;
                        else if (t.Value.Equals("using", StringComparison.OrdinalIgnoreCase))
                            StartCommand(input, f, "using", "", t.Line, pipeline); // checked by the rules (module, assembly)
                        f.CmdPos = PipelineKeywords.Contains(t.Value);
                        prev = t;
                        continue;
                    }
                    StartCommand(input, f, t.Value, "", t.Line, pipeline);
                }
                else f.CmdPos = false; // a variable, string, number or type: an expression statement
                prev = t;
                continue;
            }

            if (f.Cmd is not null) f.Cmd.Args.Add(Arg(t));
            prev = t;
        }
        return input;
    }

    private static void StartCommand(GuardInput input, Frame f, string? name, string op, int line, int pipeline, bool block = false)
    {
        var cmd = new GCmd(name, op, line, [], pipeline, block);
        input.Commands.Add(cmd);
        f.Cmd = cmd;
        f.CmdPos = false;
        f.CallOp = null;
    }

    private static void EndStatement(Frame f)
    {
        f.Cmd = null;
        f.CmdPos = true;
        f.CallOp = null;
        f.Redirect = false;
        f.FunctionName = false;
    }

    private static bool LooksNumeric(string s) => s.Length > 0 && (char.IsAsciiDigit(s[0]) || (s[0] is '-' or '.' && s.Length > 1 && char.IsAsciiDigit(s[1])));

    private static GArg Arg(Tok t) => t.Kind switch
    {
        TokKind.Word => new GArg(t.Expandable ? null : t.Value, t.Value, t.Line),
        TokKind.String => new GArg(t.Expandable ? null : t.Value, t.Value, t.Line),
        TokKind.Parameter => new GArg(t.Value, t.Value, t.Line, Param: true),
        TokKind.Splat => new GArg(null, t.Text, t.Line, Splat: true),
        _ => new GArg(null, t.Text, t.Line),
    };

    /// <summary>The same input from PowerShell's own parse (layer 2).</summary>
    public static GuardInput FromParse(ScriptParse parse)
    {
        var input = new GuardInput();
        foreach (var c in parse.Commands)
        {
            var args = new List<GArg>();
            foreach (var a in c.Arguments)
            {
                if (a is null) args.Add(new GArg(null, "", c.Line));
                else if (a.StartsWith('@')) args.Add(new GArg(null, a, c.Line, Splat: true));
                else if (a.StartsWith('-') && a.Length > 1 && char.IsAsciiLetter(a[1])) args.Add(new GArg(a, a, c.Line, Param: true));
                else args.Add(new GArg(a, a, c.Line));
                if (a is not null) input.Literals.Add((a, c.Line));
            }
            if (c.Name is not null) input.Literals.Add((c.Name, c.Line));
            input.Commands.Add(new GCmd(c.Name, c.Operator, c.Line, args, c.Pipeline));
        }
        foreach (var m in parse.Members)
        {
            input.Members.Add(new GMember(m.Member, m.Static, m.TypeName, m.Invoked, m.Line));
            if (m.Member is not null) input.Literals.Add((m.Member, m.Line));
        }
        foreach (var (name, line) in parse.Types)
        {
            input.Types.Add((name, line));
            input.Literals.Add((name, line));
        }
        return input;
    }
}
