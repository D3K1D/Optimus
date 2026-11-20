namespace OptimizationTool.Engine.Actions.Scripts;

// The seams of approved scripts (contracts/m9-ai-powers.md §5). The engine owns the rules (serialisation, guard, plan,
// approval, lifecycle, transcript, scrub); the Windows side (src/Windows/Actions/Scripts) implements the parts that touch
// the PC: the runner, PowerShell's own parser and the execution-policy read. Tests use fakes for every seam; nothing in
// the engine starts a process.

/// <summary>One line of script output as it arrived: stdout ("out") or stderr ("err").</summary>
public interface IScriptRunObserver
{
    /// <summary>The process was created, put in its job and resumed: the script is running from now.</summary>
    void Started(DateTimeOffset at, string runFolder);

    /// <summary>One output line (no line break). Called from the drain threads in arrival order.</summary>
    void Line(bool stderr, string text);
}

/// <summary>What the runner is asked to run: exactly these bytes (UTF-8 with BOM, CRLF), which must hash to <see cref="Sha256"/>.</summary>
public sealed record ScriptRunRequest(string OperationId, byte[] Bytes, string Sha256, TimeSpan Timeout, bool BypassPolicy, string RunsRoot)
{
    /// <summary>The approved plan (an elevated run names it to the broker, which records it).</summary>
    public string? PlanId { get; init; }
}

/// <summary>
/// How a run ended, as the runner saw it. <see cref="Exited"/>: the process ran and exited with <see cref="ExitCode"/>.
/// Otherwise <see cref="NotStartedCode"/> says why nothing ran (hash_mismatch, untrusted_interpreter, folder, launch_failed),
/// or <see cref="Killed"/> says the job was ended (stop, timeout or shutdown; the engine knows which).
/// </summary>
public sealed record ScriptRunResult(bool Exited, int? ExitCode, bool Killed, string? NotStartedCode, string? RunFolder, TimeSpan Duration)
{
    public static ScriptRunResult Exit(int code, string? folder, TimeSpan duration) => new(true, code, false, null, folder, duration);
    public static ScriptRunResult KilledRun(string? folder, TimeSpan duration) => new(false, null, true, null, folder, duration);
    public static ScriptRunResult NotStarted(string code, string? folder = null) => new(false, null, false, code, folder, TimeSpan.Zero);

    /// <summary>
    /// m9 §13 R2: completes when what ran the script has really ended (the elevated broker's process exited); the engine keeps
    /// the script's lease and exclusion until then. Null: already ended (the user-scope runner returns after its job is gone).
    /// </summary>
    public Task? Ended { get; init; }
}

/// <summary>
/// Runs one approved script (§5.4). The only implementation that starts a process is the Windows runner; the engine calls
/// it from exactly one place (an approved script operation). Cancelling <paramref name="stop"/> ends every process in the job.
/// </summary>
public interface IScriptRunner
{
    Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunObserver observer, CancellationToken stop);
}

/// <summary>One command PowerShell's parser found: its name (null when not a literal), how it was invoked, its literal arguments.</summary>
/// <param name="Operator">"" (plain), "&amp;" or ".".</param>
/// <param name="Arguments">Each argument's literal text, a parameter as "-Name", a splatted variable as "@name", or null when the argument is not a constant.</param>
/// <param name="Pipeline">Commands with the same number are one pipeline (a | b | c).</param>
public sealed record ParsedCommand(string? Name, string Operator, int Line, IReadOnlyList<string?> Arguments, int Pipeline = 0);

/// <summary>A member call or access the parser found: the member name (null when not a constant), whether static, the type when static.</summary>
public sealed record ParsedMember(string? Member, bool Static, string? TypeName, bool Invoked, int Line);

/// <summary>PowerShell's parse of the exact text (layer 2 of the guard). Nothing in it is executed.</summary>
public sealed record ScriptParse(IReadOnlyList<ParsedCommand> Commands, IReadOnlyList<ParsedMember> Members, IReadOnlyList<(string Name, int Line)> Types,
    IReadOnlyList<(int Line, string Code)> Errors);

/// <summary>
/// PowerShell's own parser, parse only (§5.3 layer 2). Returns null when the parser could not be used (not found,
/// untrusted, blocked, timed out, unreadable output): the host then refuses the script unless
/// <see cref="ScriptOptions.AllowGuardWithoutParser"/> says otherwise. Implementations never run the script.
/// </summary>
public interface IScriptParser
{
    Task<ScriptParse?> ParseAsync(byte[] bytes, CancellationToken ct);
}

/// <summary>
/// The PowerShell execution policy as it applies to a script Optimus runs (§5.4). <see cref="Managed"/>: a Group Policy sets
/// an execution policy or turns scripts off (every script is ineligible: managed). <see cref="NeedsBypass"/>: the local
/// policy would refuse an unsigned local script, so the run adds -ExecutionPolicy Bypass for that one process.
/// </summary>
public sealed record ScriptPolicyState(bool Managed, bool NeedsBypass);

public interface IScriptPolicy
{
    ScriptPolicyState Read();
}

/// <summary>Restore point before a script (§5.2): availability on the card, and the outcome of making one.</summary>
public sealed record RestorePointAvailability(bool Available, string? Reason);

/// <param name="Result">created | reused | failed | declined.</param>
public sealed record RestorePointOutcome(string Result, DateTimeOffset? At)
{
    /// <summary>The restore point's broker session really ended (see <see cref="ElevatedResult.Ended"/>); null: already ended.</summary>
    public Task? Ended { get; init; }
}

/// <summary>
/// The restore-point pre-step (§5.2 "Restore point"). It needs the elevated broker (a later batch): until then the host has
/// none and every script plan says <c>{available:false, reason:"flag_off"}</c>. The broker batch plugs
/// <c>restore_point.create</c> in here.
/// </summary>
public interface IRestorePointStep
{
    RestorePointAvailability Availability();
    Task<RestorePointOutcome> CreateAsync(CancellationToken ct);
}

/// <summary>Who and where this PC is, for the output scrub (§5.4): every name is replaced where it appears.</summary>
public sealed record ScrubIdentity(IReadOnlyList<string> UserNames, IReadOnlyList<string> PcNames, IReadOnlyList<string> ProfilePaths)
{
    public static ScrubIdentity Current()
    {
        var users = new List<string> { Environment.UserName };
        var pcs = new List<string> { Environment.MachineName, Environment.UserDomainName };
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) users.Add(Path.GetFileName(profile.TrimEnd('\\')));
        return new ScrubIdentity(users.Where(u => u.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            pcs.Where(p => p.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), profile.Length > 0 ? [profile] : []);
    }
}

/// <summary>The approved-scripts wiring (§5). Without a runner and a policy reader the host has no script path (available.scripts false).</summary>
public sealed class ScriptOptions
{
    public IScriptRunner? Runner { get; init; }

    /// <summary>
    /// The elevated runner (m9 §5.4: one broker session per run). Used only for a plan with elevated:true, and only with
    /// --enable-admin-actions AND <see cref="EnableElevatedScripts"/> (--enable-elevated-scripts); otherwise such a plan is
    /// admin_unavailable.
    /// </summary>
    public IScriptRunner? ElevatedRunner { get; init; }

    /// <summary>The host flag --enable-elevated-scripts (D6: off by default).</summary>
    public bool EnableElevatedScripts { get; init; }

    public IScriptParser? Parser { get; init; }
    public IScriptPolicy? Policy { get; init; }

    /// <summary>The restore-point pre-step; null until the broker batch (flag_off).</summary>
    public IRestorePointStep? RestorePoint { get; init; }

    /// <summary>Host option: accept a script screened by the C# layer alone when PowerShell's parser is unavailable. Off: fail closed.</summary>
    public bool AllowGuardWithoutParser { get; init; }

    /// <summary>The hard limit of one run (§5.4: 10 minutes).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often a running script's "Running · m:ss" progress is published.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a transcript is kept (§5.4: 30 days).</summary>
    public TimeSpan TranscriptLifetime { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The names the output scrub removes. Null: this user and PC.</summary>
    public Func<ScrubIdentity>? Identity { get; init; }

    /// <summary>The run folders' root. Null: &lt;data&gt;\script-runs.</summary>
    public string? RunsRoot { get; init; }

    public bool Configured => Runner is not null && Policy is not null;
}
