namespace OptimizationTool.Bench;

/// <summary>A part ends with a wire failure (reason from <see cref="BenchIds"/>), optionally naming the subtest.</summary>
internal sealed class BenchFailedException(string reason, string? subtest = null) : Exception(reason)
{
    public string Reason { get; } = reason;
    public string? Subtest { get; } = subtest;
}

/// <summary>One benchmark part. Returns the result, throws <see cref="BenchFailedException"/>, or throws
/// <see cref="OperationCanceledException"/> after releasing everything when the token is cancelled.</summary>
internal interface IBenchPart
{
    PartResult Run(PartContext ctx);
}

/// <summary>
/// Test-only time/size scale. Active only when the exe is started with <c>--test-scale=&lt;0.01..1&gt;</c> on its command
/// line; the Host starts the worker with no arguments, so a real run is always at scale 1. An environment variable was
/// rejected on purpose: it could leak in from the user's environment, the command line cannot. When active, the hello
/// event carries <c>"testScale"</c> so the Host can tell. Safety limits (4 GiB free-space reserve, low-memory floor of
/// 10 % available) are never scaled.
/// </summary>
internal sealed record BenchScale(double Factor)
{
    public static readonly BenchScale Full = new(1.0);
    public bool IsTest => Factor < 1.0;

    public TimeSpan Time(double seconds) => TimeSpan.FromSeconds(seconds * Factor);
    public long Bytes(long bytes, long floor) => Math.Max(floor, (long)(bytes * Factor));
    public long Count(long count, long floor) => Math.Max(floor, (long)(count * Factor));

    /// <summary>Parses <c>--test-scale=x</c> from argv. Anything else → <see cref="Full"/>.</summary>
    public static BenchScale FromArgs(string[] args)
    {
        foreach (var a in args)
        {
            const string prefix = "--test-scale=";
            if (a.StartsWith(prefix, StringComparison.Ordinal)
                && double.TryParse(a.AsSpan(prefix.Length), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f)
                && f >= 0.01 && f < 1.0)
                return new BenchScale(f);
        }
        return Full;
    }
}

/// <summary>What a running part sees: its command, the stop token, the scale, and progress/live emitters.</summary>
internal sealed class PartContext(RunCommand command, CancellationToken token, BenchScale scale, EventWriter? events)
{
    public RunCommand Command { get; } = command;
    public string Part => Command.Part;
    public CancellationToken Token { get; } = token;
    public BenchScale Scale { get; } = scale;
    public string? Repeat => Command.Repeat;

    public bool Runs(string subtest) => Repeat is null || Repeat == subtest;

    public void Progress(string subtest, double progress, double? liveScore, double etaSec) =>
        events?.Progress(Part, subtest, progress, liveScore, etaSec);

    public void Live(params KeyValuePair<string, double>[] values) => events?.Live(Part, values);

    public static KeyValuePair<string, double> V(string id, double value) => new(id, value);

    /// <summary>Sleeps up to <paramref name="time"/>, waking early on stop. Throws when stopped.</summary>
    public void Sleep(TimeSpan time)
    {
        if (Token.WaitHandle.WaitOne(time)) Token.ThrowIfCancellationRequested();
    }
}
