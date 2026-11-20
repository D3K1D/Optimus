using System.Security.Cryptography;
using OptimizationTool.Engine.Actions.Scripts;

namespace OptimizationTool.Tests.Scripts;

/// <summary>
/// The fake runner (m9 §5.4 seam). It never starts a process: it writes the bytes it is given to a file in a new folder
/// under the runs root (inside the test's temporary data folder), re-reads and re-hashes that file as the real runner must,
/// then plays back canned output lines and an exit code, or waits on <see cref="Gate"/> until released or cancelled.
/// </summary>
public sealed class FakeScriptRunner : IScriptRunner
{
    public int Calls;
    public int Started;
    public readonly List<ScriptRunRequest> Requests = [];
    public List<(bool Err, string Text)> Lines { get; set; } = [(false, "hello"), (true, "a warning")];
    public int ExitCode { get; set; }
    public string? NotStartedCode { get; set; }
    /// <summary>When set, the run waits for it (or for cancellation) after its output.</summary>
    public TaskCompletionSource? Gate { get; set; }
    /// <summary>Changes the file on disk between writing and launching (a swapped file).</summary>
    public Action<string>? Tamper { get; set; }
    public volatile bool SawCancellation;
    public string? LastFolder;

    public async Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunObserver observer, CancellationToken stop)
    {
        Interlocked.Increment(ref Calls);
        lock (Requests) Requests.Add(request);
        if (NotStartedCode is { } code) return ScriptRunResult.NotStarted(code);
        var folder = Path.Combine(request.RunsRoot, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(folder);
        LastFolder = folder;
        var file = Path.Combine(folder, "script.ps1");
        await File.WriteAllBytesAsync(file, request.Bytes, CancellationToken.None);
        Tamper?.Invoke(file);
        var onDisk = await File.ReadAllBytesAsync(file, CancellationToken.None);
        if (Convert.ToHexStringLower(SHA256.HashData(onDisk)) != request.Sha256) return ScriptRunResult.NotStarted("hash_mismatch", folder);
        observer.Started(DateTimeOffset.UtcNow, folder);
        Interlocked.Increment(ref Started);
        foreach (var (err, text) in Lines) observer.Line(err, text);
        if (Gate is { } gate)
        {
            try { await gate.Task.WaitAsync(stop); }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                return ScriptRunResult.KilledRun(folder, TimeSpan.FromMilliseconds(5));
            }
        }
        return ScriptRunResult.Exit(ExitCode, folder, TimeSpan.FromMilliseconds(5));
    }
}

/// <summary>PowerShell's parse, faked: a clean parse by default; a callback runs while "parsing" (to race the task).</summary>
public sealed class FakeScriptParser : IScriptParser
{
    public ScriptParse? Result { get; set; } = new([], [], [], []);
    public Action? During { get; set; }
    public int Calls;

    public Task<ScriptParse?> ParseAsync(byte[] bytes, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        During?.Invoke();
        return Task.FromResult(Result);
    }
}

public sealed class FakeScriptPolicy : IScriptPolicy
{
    public volatile bool Managed;
    public volatile bool NeedsBypass = true;
    public ScriptPolicyState Read() => new(Managed, NeedsBypass);
}

/// <summary>The restore-point pre-step, faked (the real one needs the broker batch).</summary>
public sealed class FakeRestorePoint : IRestorePointStep
{
    public bool Available { get; set; } = true;
    public string? Reason { get; set; }
    public string Result { get; set; } = "created";
    public DateTimeOffset? At { get; set; }
    public int Calls;

    public RestorePointAvailability Availability() => new(Available, Available ? null : Reason ?? "protection_off");

    public Task<RestorePointOutcome> CreateAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new RestorePointOutcome(Result, Result is "created" or "reused" ? At ?? DateTimeOffset.UtcNow : null));
    }
}
