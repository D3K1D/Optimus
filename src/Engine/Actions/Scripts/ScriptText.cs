using System.Security.Cryptography;
using System.Text;

namespace OptimizationTool.Engine.Actions.Scripts;

/// <summary>A script in its one serialisation (§5.1 rule 3), or the rule that refused it (§5.7 codes).</summary>
public sealed record ScriptSerialisation(bool Ok, string? Rule, int? Line, string Text, IReadOnlyList<string> Lines, byte[] Bytes, string Sha256)
{
    public static ScriptSerialisation Refused(string rule, int? line) => new(false, rule, line, "", [], [], "");
}

/// <summary>
/// The exact bytes of an approved script (contracts/m9-ai-powers.md §5.1 rules 3–4). One serialisation exists: UTF-8 with
/// BOM and CRLF line endings. Input is plain text only (printable ASCII, tab, CR, LF), at most 200 lines of at most 200
/// characters and 8 KB with the BOM; anything else is refused, never trimmed, so nothing on the card can be hidden or
/// reordered. The sha256 is over exactly the bytes that run; <see cref="ScriptSerialisation.Text"/> is what the card shows.
/// </summary>
public static class ScriptText
{
    public const int MaxLines = 200, MaxLineChars = 200, MaxBytes = 8192;
    public const string Empty = "empty", NonAscii = "non_ascii", TooManyLines = "too_many_lines", LineTooLong = "line_too_long", TooLarge = "too_large";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static ScriptSerialisation Serialise(string? input)
    {
        if (input is null || input.All(c => c is ' ' or '\t' or '\r' or '\n')) return ScriptSerialisation.Refused(Empty, null);
        var line = 1;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= input.Length || input[i + 1] != '\n'))) { line++; continue; }
            if (c is '\t' or '\r' || (c >= 32 && c <= 126)) continue;
            return ScriptSerialisation.Refused(NonAscii, line);
        }
        var text = input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = text.Split("\r\n").ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > MaxLines) return ScriptSerialisation.Refused(TooManyLines, MaxLines + 1);
        for (var j = 0; j < lines.Count; j++)
            if (lines[j].Length > MaxLineChars) return ScriptSerialisation.Refused(LineTooLong, j + 1);
        var bytes = new byte[Bom.Length + text.Length];
        Bom.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes(text, 0, text.Length, bytes, Bom.Length);
        if (bytes.Length > MaxBytes) return ScriptSerialisation.Refused(TooLarge, null);
        return new ScriptSerialisation(true, null, null, text, lines, bytes, Hash(bytes));
    }

    /// <summary>Lowercase hex SHA-256 of the bytes.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
