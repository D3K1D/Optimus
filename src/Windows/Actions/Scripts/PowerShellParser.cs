using System.Text;
using System.Text.Json;
using OptimizationTool.Engine.Actions.Scripts;

namespace OptimizationTool.Windows.Actions.Scripts;

/// <summary>
/// Layer 2 of the guard (contracts/m9-ai-powers.md §5.3): PowerShell's own parser, parse only. The exact bytes are staged
/// like a run (new folder, file held read-only, re-hashed), then Windows PowerShell 5.1 (verified as for a run, the same
/// from-scratch environment) runs one fixed command of ours, passed as -EncodedCommand: <c>[Parser]::ParseFile</c> on that
/// file, which never executes it, printing the commands, members, types and parse errors as JSON. Any failure (interpreter
/// not trusted, blocked, timed out, output unreadable or over 256 KB) returns null, and the host then refuses the script.
/// Never run by the test suite.
/// </summary>
public sealed class PowerShellParser(string runsRoot, Func<string, IDisposable>? trustedRoot = null) : IScriptParser
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    public const int MaxOutput = 256 * 1024;

    /// <summary>The fixed parse command; {0} is the staged file's path as a single-quoted PowerShell literal.</summary>
    internal const string Command = """
        $ErrorActionPreference = 'Stop'
        $t = $null; $e = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile({0}, [ref]$t, [ref]$e)
        $pipes = @{}
        $cmds = @(foreach ($c in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
          $list = New-Object System.Collections.ArrayList
          $first = $true
          foreach ($el in $c.CommandElements) {
            if ($first) { $first = $false; continue }
            if ($el -is [System.Management.Automation.Language.StringConstantExpressionAst]) { [void]$list.Add($el.Value) }
            elseif ($el -is [System.Management.Automation.Language.CommandParameterAst]) {
              [void]$list.Add('-' + $el.ParameterName)
              if ($el.Argument -ne $null) { if ($el.Argument -is [System.Management.Automation.Language.StringConstantExpressionAst]) { [void]$list.Add($el.Argument.Value) } else { [void]$list.Add($null) } }
            }
            elseif ($el -is [System.Management.Automation.Language.VariableExpressionAst] -and $el.Splatted) { [void]$list.Add('@' + $el.VariablePath.UserPath) }
            elseif ($el -is [System.Management.Automation.Language.ConstantExpressionAst]) { [void]$list.Add([string]$el.Value) }
            else { [void]$list.Add($null) }
          }
          $key = if ($c.Parent) { $c.Parent.Extent.StartOffset } else { 0 }
          if (-not $pipes.ContainsKey($key)) { $pipes[$key] = $pipes.Count }
          $op = [string]$c.InvocationOperator
          @{ name = $c.GetCommandName(); op = $(if ($op -eq 'Ampersand') { '&' } elseif ($op -eq 'Dot') { '.' } else { '' }); line = $c.Extent.StartLineNumber; args = @($list); pipeline = $pipes[$key] }
        })
        $members = @(foreach ($m in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.MemberExpressionAst] }, $true)) {
          $name = if ($m.Member -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $m.Member.Value } else { $null }
          $type = if ($m.Static -and $m.Expression -is [System.Management.Automation.Language.TypeExpressionAst]) { $m.Expression.TypeName.FullName } else { $null }
          @{ member = $name; static = [bool]$m.Static; type = $type; invoked = [bool]($m -is [System.Management.Automation.Language.InvokeMemberExpressionAst]); line = $m.Extent.StartLineNumber }
        })
        $types = @(foreach ($x in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TypeExpressionAst] -or $n -is [System.Management.Automation.Language.TypeConstraintAst] }, $true)) {
          @{ name = $x.TypeName.FullName; line = $x.Extent.StartLineNumber }
        })
        $errs = @(foreach ($x in $e) { @{ line = $x.Extent.StartLineNumber; code = $x.ErrorId } })
        [Console]::Out.Write((@{ commands = $cmds; members = $members; types = $types; errors = $errs } | ConvertTo-Json -Depth 6 -Compress))
        """;

    public async Task<ScriptParse?> ParseAsync(byte[] bytes, CancellationToken ct)
    {
        var (exe, _) = PowerShellInterpreter.Locate();
        if (exe is null) return null;
        var sha = ScriptText.Hash(bytes);
        var (staged, _) = ScriptStage.Prepare(Path.Combine(runsRoot, "parse"), bytes, sha, trustedRoot: trustedRoot);
        if (staged is null) return null;
        try
        {
            using (staged)
            {
                var literal = "'" + staged.ScriptPath.Replace("'", "''", StringComparison.Ordinal) + "'";
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Command.Replace("{0}", literal, StringComparison.Ordinal)));
                string[] args = ["-NoProfile", "-NonInteractive", "-InputFormat", "None", "-EncodedCommand", encoded];
                using var process = ScriptProcess.Start(exe, PowerShellInterpreter.CommandLine(exe, args), PowerShellInterpreter.Environment(staged.Folder), staged.Folder);
                var output = new StringBuilder();
                var tooLong = false;
                var encoding = ScriptProcess.OutputEncoding();
                var drain = Task.Run(() => ScriptProcess.DrainAsync(process.Stdout, encoding, l =>
                {
                    lock (output)
                    {
                        if (output.Length + l.Length > MaxOutput) tooLong = true;
                        else output.Append(l);
                    }
                }, maxLine: MaxOutput), CancellationToken.None);
                var errors = Task.Run(() => ScriptProcess.DrainAsync(process.Stderr, encoding, _ => { }), CancellationToken.None);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(Limit);
                try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
                try { await Task.WhenAll(drain, errors).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { return null; }
                if (process.ExitCode != 0 || tooLong) return null;
                lock (output) return Read(output.ToString());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { return null; }
        finally
        {
            try { Directory.Delete(staged.Folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The parse command's JSON → <see cref="ScriptParse"/>, strictly; anything unexpected → null.</summary>
    internal static ScriptParse? Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Null ? null : throw new FormatException();
            static int Int(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : throw new FormatException();
            static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
                root.TryGetProperty(name, out var a) ? a.ValueKind switch { JsonValueKind.Array => a.EnumerateArray(), JsonValueKind.Null => [], JsonValueKind.Object or JsonValueKind.String => [a], _ => throw new FormatException() } : [];
            var commands = Items(root, "commands").Select(c => new ParsedCommand(Str(c.GetProperty("name")), Str(c.GetProperty("op")) ?? "", Int(c.GetProperty("line")),
                [.. Items(c, "args").Select(Str)], Int(c.GetProperty("pipeline")))).ToList();
            var members = Items(root, "members").Select(m => new ParsedMember(Str(m.GetProperty("member")), m.GetProperty("static").GetBoolean(), Str(m.GetProperty("type")),
                m.GetProperty("invoked").GetBoolean(), Int(m.GetProperty("line")))).ToList();
            var types = Items(root, "types").Select(t => (Str(t.GetProperty("name")) ?? "", Int(t.GetProperty("line")))).ToList();
            var errors = Items(root, "errors").Select(e => (Int(e.GetProperty("line")), Str(e.GetProperty("code")) ?? "error")).ToList();
            return new ScriptParse(commands, members, types, errors);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
