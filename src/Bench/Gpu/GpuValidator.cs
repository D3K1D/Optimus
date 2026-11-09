namespace OptimizationTool.Bench.Gpu;

/// <summary>
/// Correctness checks for the GPU kernels (§4): FMA output vs a CPU reference computed in double with relative
/// tolerance 1e-3; copy output compared exactly against a fixed integer pattern.
/// </summary>
internal static class GpuValidator
{
    public const double RelativeTolerance = 1e-3;
    public const float A = 0.999f, B = 0.001f;   // contraction towards 1: rounding error stays ~1e-5 relative
    public const uint ValidateIterations = 64;   // 512 steps per chain: outputs still depend on the thread id
    public const uint ValidateThreads = 4096;

    /// <summary>Mirror of fma.hlsl for one thread, in double.</summary>
    public static double FmaReference(uint id, uint iterations, float a = A, float b = B)
    {
        double da = a, db = b;
        var x0 = (id & 1023u) / 1024.0;
        Span<double> x = [x0, x0 + 0.125, x0 + 0.25, x0 + 0.375, x0 + 0.5, x0 + 0.625, x0 + 0.75, x0 + 0.875];
        for (uint i = 0; i < iterations * 8; i++)
            for (var c = 0; c < 8; c++) x[c] = x[c] * da + db;
        return ((x[0] + x[1]) + (x[2] + x[3])) + ((x[4] + x[5]) + (x[6] + x[7]));
    }

    public static bool WithinTolerance(double gpu, double reference, double relative = RelativeTolerance) =>
        double.IsFinite(gpu) && double.IsFinite(reference)
        && Math.Abs(gpu - reference) <= relative * Math.Max(Math.Abs(reference), double.Epsilon);

    /// <summary>Index of the first output outside tolerance, or -1 when all match.</summary>
    public static int CheckFma(ReadOnlySpan<float> outputs, uint iterations, float a = A, float b = B)
    {
        for (var i = 0; i < outputs.Length; i++)
            if (!WithinTolerance(outputs[i], FmaReference((uint)i, iterations, a, b))) return i;
        return -1;
    }

    /// <summary>Copy source pattern: a fixed integer hash of the 32-bit word index (no floating point).</summary>
    public static uint CopyPattern(long word)
    {
        var x = (uint)word * 0x9E3779B1u;
        x ^= x >> 15;
        x *= 0x85EBCA77u;
        return x ^ (x >> 13) ^ (uint)(word >> 32);
    }

    /// <summary>Index of the first word that differs from the pattern (offset by <paramref name="firstWord"/>), or -1.</summary>
    public static long CheckCopy(ReadOnlySpan<uint> words, long firstWord)
    {
        for (var i = 0; i < words.Length; i++)
            if (words[i] != CopyPattern(firstWord + i)) return firstWord + i;
        return -1;
    }
}
