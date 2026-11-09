using System.Buffers;
using System.Text;
using System.Text.Json;

namespace OptimizationTool.Bench;

// Worker wire (contracts/m6-benchmark.md §4). The authoritative description is the comment block in
// src/Engine/Bench/BenchSeams.cs; this project does not reference the Engine, so the constants it needs are copied here.

/// <summary>Part and subtest ids (benchVersion 1), copied from BenchSeams.cs.</summary>
internal static class BenchIds
{
    public const string Ram = "ram", Cpu = "cpu", Gpu = "gpu", Disk = "disk";
    public const string BenchVersion = "1";
    public const string WireVersion = "1";

    public static readonly IReadOnlyDictionary<string, string[]> Subtests = new Dictionary<string, string[]>
    {
        [Cpu] = ["single", "multi"],
        [Gpu] = ["fma", "copy"],
        [Ram] = ["read", "write", "copy", "latency"],
        [Disk] = ["seqWrite", "seqRead", "rand4k"],
    };

    /// <summary>Failure reasons on the wire.</summary>
    public const string CheckFailed = "check_failed", GpuReset = "gpu_reset", TooSlow = "too_slow", LowMemory = "low_memory",
        NeedsSpace = "needs_space", Unsupported = "unsupported", Error = "error";
}

internal abstract record Command;
internal sealed record StopCommand : Command;
internal sealed record ExitCommand : Command;
internal sealed record RunCommand(string Part, string BenchVersion, string? Folder, long? RamBudgetBytes, string? GpuLuid,
    string? Repeat) : Command;

internal static class Wire
{
    /// <summary>Longest accepted line in either direction (BenchSeams.cs: ≤ 64 KiB per line).</summary>
    public const int MaxLineBytes = 64 * 1024;

    private static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = 4 };

    /// <summary>Parses one host → worker line. Null when the line is not a valid command.</summary>
    public static Command? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || Encoding.UTF8.GetByteCount(line) > MaxLineBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(line, DocOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            switch (Str(root, "cmd"))
            {
                case "stop": return new StopCommand();
                case "exit": return new ExitCommand();
                case "run":
                    var part = Str(root, "part");
                    if (part is null) return null;
                    long? budget = null;
                    if (root.TryGetProperty("ramBudgetBytes", out var b) && b.ValueKind == JsonValueKind.Number)
                    {
                        if (!b.TryGetInt64(out var v)) return null;
                        budget = v;
                    }
                    return new RunCommand(part, Str(root, "benchVersion") ?? "", Str(root, "folder"), budget,
                        Str(root, "gpuLuid"), Str(root, "repeat"));
                default: return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>One scored subtest of a part's result.</summary>
internal sealed record SubtestValue(string Id, double Value, string Unit);

/// <summary>A part's result: all subtests (or only the repeated one) plus info (string or long values).</summary>
internal sealed record PartResult(IReadOnlyList<SubtestValue> Subtests, IReadOnlyList<KeyValuePair<string, object>> Info);

/// <summary>
/// Writes worker → host events: one UTF-8 JSON object per line, flushed per line, serialized by a lock so the heartbeat
/// thread and the part thread never interleave. A broken stdout (host gone) is swallowed; the worker then exits on stdin EOF.
/// </summary>
internal sealed class EventWriter(Stream output)
{
    private readonly Lock _lock = new();
    private bool _broken;

    public void Hello(int pid, double? testScale) => Write(w =>
    {
        w.WriteString("ev", "hello");
        w.WriteString("version", BenchIds.WireVersion);
        w.WriteNumber("pid", pid);
        // Additive, present only in test mode (--test-scale), so a scaled run can never pass as a real one.
        if (testScale is { } s) WriteNum(w, "testScale", s);
    });

    public void Heartbeat() => Write(w => w.WriteString("ev", "hb"));

    public void Progress(string part, string subtest, double progress, double? liveScore, double etaSec) => Write(w =>
    {
        w.WriteString("ev", "progress");
        w.WriteString("part", part);
        w.WriteString("subtest", subtest);
        WriteNum(w, "progress", Math.Clamp(progress, 0, 1));
        WriteNum(w, "liveScore", liveScore);
        w.WriteNumber("etaSec", (int)Math.Ceiling(Math.Max(0, double.IsFinite(etaSec) ? etaSec : 0)));
    });

    public void Live(string part, IReadOnlyList<KeyValuePair<string, double>> values) => Write(w =>
    {
        w.WriteString("ev", "live");
        w.WriteString("part", part);
        w.WriteStartObject("values");
        foreach (var (k, v) in values) WriteNum(w, k, v);
        w.WriteEndObject();
    });

    public void Result(string part, PartResult result) => Write(w =>
    {
        w.WriteString("ev", "result");
        w.WriteString("part", part);
        w.WriteStartObject("subtests");
        foreach (var s in result.Subtests)
        {
            w.WriteStartObject(s.Id);
            WriteNum(w, "value", s.Value);
            w.WriteString("unit", s.Unit);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteStartObject("info");
        foreach (var (k, v) in result.Info)
        {
            switch (v)
            {
                case string s: w.WriteString(k, s); break;
                case long l: w.WriteNumber(k, l); break;
                case int i: w.WriteNumber(k, i); break;
                case double d: WriteNum(w, k, d); break;
                default: w.WriteNull(k); break;
            }
        }
        w.WriteEndObject();
    });

    public void Failed(string part, string reason, string? subtest) => Write(w =>
    {
        w.WriteString("ev", "failed");
        w.WriteString("part", part);
        w.WriteString("reason", reason);
        if (subtest is null) w.WriteNull("subtest"); else w.WriteString("subtest", subtest);
    });

    public void Stopped(string? part) => Write(w =>
    {
        w.WriteString("ev", "stopped");
        if (part is null) w.WriteNull("part"); else w.WriteString("part", part);
    });

    /// <summary>Non-finite numbers are written as null (JSON has no NaN); finite ones rounded to 3 decimals.</summary>
    private static void WriteNum(Utf8JsonWriter w, string name, double? v)
    {
        if (v is { } d && double.IsFinite(d)) w.WriteNumber(name, Math.Round(d, 3));
        else w.WriteNull(name);
    }

    internal static byte[] Serialize(Action<Utf8JsonWriter> body)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { SkipValidation = false }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return buf.WrittenSpan.ToArray();
    }

    private void Write(Action<Utf8JsonWriter> body)
    {
        var bytes = Serialize(body);
        if (bytes.Length >= Wire.MaxLineBytes) return; // never happens with these shapes; never emit an oversize line
        lock (_lock)
        {
            if (_broken) return;
            try
            {
                output.Write(bytes);
                output.WriteByte((byte)'\n');
                output.Flush();
            }
            catch (IOException) { _broken = true; }
            catch (ObjectDisposedException) { _broken = true; }
        }
    }
}

/// <summary>Short diagnostic codes to stderr, capped at 64 KiB total (BenchSeams.cs). Never free text from the PC.</summary>
internal static class Diag
{
    private static readonly Lock Gate = new();
    private static long _written;
    internal static TextWriter? Override;

    public static void Code(string code)
    {
        lock (Gate)
        {
            if (_written + code.Length + 1 > 64 * 1024) return;
            _written += code.Length + 1;
            try { (Override ?? Console.Error).WriteLine(code); } catch (IOException) { }
        }
    }
}

/// <summary>Reads '\n'-terminated UTF-8 lines with a length cap; an over-long line is skipped whole. Null at EOF.</summary>
internal sealed class BoundedLineReader(Stream input)
{
    private readonly byte[] _buf = new byte[4096];
    private int _pos, _len;

    public string? ReadLine()
    {
        var line = new MemoryStream();
        var overflow = false;
        while (true)
        {
            if (_pos == _len)
            {
                _len = input.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0)
                {
                    _len = 0;
                    if (line.Length == 0 && !overflow) return null;
                    return overflow ? "" : Decode(line);
                }
            }
            var nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
            var end = nl < 0 ? _len : nl;
            if (!overflow)
            {
                line.Write(_buf, _pos, end - _pos);
                if (line.Length > Wire.MaxLineBytes) { overflow = true; line.SetLength(0); }
            }
            _pos = nl < 0 ? _len : nl + 1;
            if (nl >= 0) return overflow ? "" : Decode(line);
        }
    }

    // A leading U+FEFF (UTF-8 BOM some writers put before the first line) is dropped, like a trailing '\r'.
    private static string Decode(MemoryStream s) => Encoding.UTF8.GetString(s.GetBuffer(), 0, (int)s.Length).TrimStart('﻿').TrimEnd('\r');
}
