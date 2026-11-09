using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OptimizationTool.Bench.Cpu;

/// <summary>
/// CPU kernels for benchVersion 1 (contracts/m6-benchmark.md §4). Inputs are generated from fixed seeds; every check
/// is an integer/byte comparison (no floating point). The expected values are pinned below and re-derived at the start
/// of each run (a mismatch at start is also check_failed). Changing any input, size or seed is a benchVersion change.
/// </summary>
internal static class CpuKernels
{
    public const int ShaBytes = 1 << 20;          // SHA-256 over a fixed 1 MiB buffer
    public const int DeflateTextBytes = 128 << 10; // Deflate of 128 KiB of fixed synthetic text
    public const int SortCount = 1 << 16;          // integer sort of 65,536 fixed pseudo-random ints (256 KiB)

    public const ulong ShaSeed = 0x5348413235360001UL, TextSeed = 0x4445464C41540001UL, SortSeed = 0x534F525431000001UL;

    // Pinned expected checksums (benchVersion 1); tests assert the generators still produce them.
    public const string ExpectedShaHex = "1A4E33DC30130A5A2A2D16E2859BC1E264BB9ED2BED887E196374037348BEBE9";
    public const ulong ExpectedTextFnv = 0x176824F934AA0D68UL;
    public const ulong ExpectedSortChecksum = 0x352950AAD20DF330UL;

    /// <summary>
    /// One round per thread = <see cref="ShaPerRound"/> SHA-256 passes, 1 Deflate round trip, 1 sort. With these counts
    /// the three kernels take roughly equal time on the reference PC.
    /// </summary>
    public const int ShaPerRound = 8;

    /// <summary>
    /// Score weights (benchVersion 1): score = Σ weight × iterations / second, in "ops/s". One op = the work of one
    /// SHA-256 pass over 1 MiB. Each weight is that kernel's per-iteration time relative to one SHA-256 pass, measured
    /// single-threaded on the reference PC (dev PC, Nov 2026) and frozen, so a second of any kernel's work scores about
    /// the same. Measured: SHA-256 0.382 ms, Deflate round trip 2.80 ms, sort 2.52 ms.
    /// </summary>
    public const double ShaWeight = 1.0, DeflateWeight = 7.3, SortWeight = 6.6;

    public static byte[] ShaInput()
    {
        var b = new byte[ShaBytes];
        var r = new SplitMix64(ShaSeed);
        for (var i = 0; i < b.Length; i += 8) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i), r.Next());
        return b;
    }

    private static readonly string[] Words =
    [
        "the", "quick", "system", "memory", "thread", "cache", "packet", "driver", "kernel", "buffer", "window", "process",
        "update", "service", "network", "storage", "volume", "sector", "render", "shader", "vector", "matrix", "signal",
        "battery", "cooling", "fan", "clock", "voltage", "and", "of", "to", "in", "is", "for", "with", "on", "at", "by",
        "optimus", "benchmark", "result", "score", "stable", "fixed", "seed", "random", "integer", "checksum",
    ];

    /// <summary>Deterministic ASCII text: words from a fixed list, sentences, newlines; exactly 128 KiB.</summary>
    public static byte[] DeflateInput()
    {
        var r = new SplitMix64(TextSeed);
        var sb = new StringBuilder(DeflateTextBytes + 64);
        var inSentence = 0;
        while (sb.Length < DeflateTextBytes)
        {
            var w = Words[(int)r.Below((ulong)Words.Length)];
            if (inSentence == 0) sb.Append(char.ToUpperInvariant(w[0])).Append(w, 1, w.Length - 1);
            else sb.Append(w);
            inSentence++;
            // A number now and then keeps the text from being too repetitive.
            if (r.Below(16) == 0) sb.Append(' ').Append(r.Below(100000));
            if (inSentence > 4 && r.Below(8) == 0)
            {
                sb.Append(r.Below(5) == 0 ? ".\n" : ". ");
                inSentence = 0;
            }
            else sb.Append(' ');
        }
        return Encoding.ASCII.GetBytes(sb.ToString(0, DeflateTextBytes));
    }

    public static int[] SortInput()
    {
        var r = new SplitMix64(SortSeed);
        var a = new int[SortCount];
        for (var i = 0; i < a.Length; i++) a[i] = (int)r.Next();
        return a;
    }

    public static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        var h = 0xCBF29CE484222325UL;
        foreach (var b in data) h = (h ^ b) * 0x100000001B3UL;
        return h;
    }

    /// <summary>Position-weighted sum: equals the constant only if the array holds exactly the sorted input.</summary>
    public static ulong SortChecksum(ReadOnlySpan<int> a)
    {
        ulong h = 0;
        for (var i = 0; i < a.Length; i++) h += (ulong)(uint)a[i] * (ulong)(i + 1);
        return h;
    }

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
}

/// <summary>Per-thread kernel state: own scratch buffers, shared read-only inputs. Each Run* returns false on mismatch.</summary>
internal sealed class CpuKernelState
{
    private readonly byte[] _sha;
    private readonly byte[] _shaExpected;
    private readonly byte[] _text;
    private readonly ulong _textExpected;
    private readonly int[] _sortSource;
    private readonly ulong _sortExpected;
    private readonly int[] _sortWork = new int[CpuKernels.SortCount];
    private readonly byte[] _hash = new byte[32];
    private readonly MemoryStream _compressed = new(CpuKernels.DeflateTextBytes);
    private readonly byte[] _roundTrip = new byte[CpuKernels.DeflateTextBytes + 1];

    public CpuKernelState(CpuInputs inputs)
    {
        _sha = inputs.Sha;
        _shaExpected = inputs.ShaExpected;
        _text = inputs.Text;
        _textExpected = inputs.TextExpected;
        _sortSource = inputs.Sort;
        _sortExpected = inputs.SortExpected;
    }

    public bool RunSha()
    {
        SHA256.HashData(_sha, _hash);
        return _hash.AsSpan().SequenceEqual(_shaExpected);
    }

    /// <summary>Compress then decompress; the round trip must give back the exact text (length + FNV-1a).</summary>
    public bool RunDeflate()
    {
        _compressed.SetLength(0);
        using (var z = new DeflateStream(_compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(_text);
        _compressed.Position = 0;
        var total = 0;
        using (var d = new DeflateStream(_compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            int n;
            while (total < _roundTrip.Length && (n = d.Read(_roundTrip, total, _roundTrip.Length - total)) > 0) total += n;
        }
        return total == _text.Length && CpuKernels.Fnv1a(_roundTrip.AsSpan(0, total)) == _textExpected;
    }

    public bool RunSort()
    {
        _sortSource.CopyTo(_sortWork, 0);
        Array.Sort(_sortWork);
        return CpuKernels.SortChecksum(_sortWork) == _sortExpected;
    }
}

/// <summary>Inputs and their expected checksums, built once per run and verified against the pinned constants.</summary>
internal sealed record CpuInputs(byte[] Sha, byte[] ShaExpected, byte[] Text, ulong TextExpected, int[] Sort, ulong SortExpected)
{
    /// <summary>Builds inputs and derives expected values. Null subtest-free failure when they differ from the pins.</summary>
    public static CpuInputs Create()
    {
        var sha = CpuKernels.ShaInput();
        var text = CpuKernels.DeflateInput();
        var sort = CpuKernels.SortInput();
        var sorted = (int[])sort.Clone();
        Array.Sort(sorted);
        return new CpuInputs(sha, SHA256.HashData(sha), text, CpuKernels.Fnv1a(text), sort, CpuKernels.SortChecksum(sorted));
    }

    public bool MatchesPins() =>
        CpuKernels.Hex(ShaExpected) == CpuKernels.ExpectedShaHex
        && TextExpected == CpuKernels.ExpectedTextFnv
        && SortExpected == CpuKernels.ExpectedSortChecksum;
}
