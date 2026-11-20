using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker;

/// <summary>The one write: INetFwPolicy2.FirewallEnabled[NET_FW_PROFILE2_PUBLIC] in the local store. Nothing else.</summary>
internal sealed class WindowsPublicFirewallWriter : IPublicFirewallWriter
{
    private const int NET_FW_PROFILE2_PUBLIC = 4;

    public void SetPublicEnabled(bool enabled)
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        var policy = Activator.CreateInstance(type)!;
        try
        {
            type.InvokeMember("FirewallEnabled", BindingFlags.SetProperty, null, policy, [NET_FW_PROFILE2_PUBLIC, enabled], CultureInfo.InvariantCulture);
        }
        finally { Marshal.ReleaseComObject(policy); }
    }
}

/// <summary>
/// The protected journal (<see cref="BrokerJournalFiles"/>) in <c>%ProgramData%\OptimizationTool\journal</c>.
/// Trust root (m9 §13 R1): the folders are a <see cref="TrustedChain"/> from %ProgramData%: each is created by the broker
/// with its security at creation (owner Administrators; SYSTEM + Administrators full; the interactive user read; protected),
/// or, if it exists, validated through a held handle that does not follow reparse points; one that fails is retired
/// (<c>&lt;name&gt;.untrusted-…</c>) and replaced, never re-secured or adopted, and if it can't be retired nothing is written.
/// The chain stays held for the life of this object. Records are created with CreateNew, never overwritten.
/// Provenance (m9 §3.5 "as built"): the broker trusts only what it can prove it wrote. Its key (<see cref="BrokerJournalFiles.KeyFile"/>,
/// 32 random bytes, SYSTEM and Administrators only) is read through one handle that is validated first (regular file, not a
/// reparse point, owner Administrators/SYSTEM, protected, only they have access). When a trusted folder has no valid key
/// the broker moves every entry into a <c>quarantine-*</c> subfolder that nothing reads, then creates its key with its
/// security at creation. Every record it writes carries a MAC with that key; every record it reads must carry one and
/// belong to the session and step it is read as, or it is ignored (and counted in <see cref="Rejected"/>).
/// Across brokers (m9 review S3b): the key is read, and on first use the folder quarantined and the key created, only
/// while holding the journal lock (<see cref="FileNonceLedger.LockName"/>, the lock the nonce ledger takes, with the same
/// bounded wait and secret check). Under it the key is read again, so a key another broker just made is adopted, never
/// quarantined. The lock is released before <see cref="EnsureDirectory"/> returns, so the ledger taking it next can't
/// deadlock. If it can't be taken, or isn't a secret, nothing is moved or written and this throws (journal_failed).
/// The lock file itself is never quarantined.
/// </summary>
internal sealed class ProtectedJournal(string trustRoot, string root, SecurityIdentifier user, ITrustedFileSystem? files = null,
    TimeSpan? lockTimeout = null, TimeProvider? time = null, Action<TimeSpan>? sleep = null)
    : IBrokerJournal, IBrokerScriptJournal, IBrokerReadJournal, IDisposable
{
    private readonly ITrustedFileSystem _files = files ?? new WindowsTrustedFileSystem(user);
    private readonly TimeSpan _lockTimeout = lockTimeout ?? FileNonceLedger.DefaultLockTimeout;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;
    private readonly Lock _gate = new();
    private TrustedChain? _chain;
    private byte[]? _key;
    private int _rejected;

    internal string Root => root;
    internal ITrustedFileSystem Files => _files;

    /// <summary>Records found in the folder that were ignored: no MAC, a wrong one, or another session's or step's.</summary>
    public int Rejected => Volatile.Read(ref _rejected);

    private void Reject(string _) => Interlocked.Increment(ref _rejected);

    // ---- elevated scripts (m9 §5.4): <id>.script.json before the run, <id>.script-end.json after it.

    public bool Seen(string operationId)
    {
        var key = Key();
        foreach (var file in Directory.EnumerateFiles(root, "rcpt-*.script.json").Take(5000))
            if (BrokerJournalFiles.Authentic(root, Path.GetFileName(file), key, Reject) is { } o && o["operationId"] is JsonValue v
                && v.TryGetValue<string>(out var id) && id == operationId) return true;
        return false;
    }

    public string Begin(BrokerScriptRequest request, DateTimeOffset at)
    {
        var id = BrokerProtocol.NewReceiptId();
        Create(id + ".script.json", new JsonObject
        {
            ["receiptId"] = id, ["planId"] = request.PlanId, ["operationId"] = request.OperationId, ["sha256"] = request.Sha256, ["at"] = At(at), ["userSid"] = user.Value,
        });
        return id;
    }

    public void End(string receiptId, string state, int? exitCode, DateTimeOffset at) =>
        Create(receiptId + ".script-end.json", new JsonObject { ["receiptId"] = receiptId, ["state"] = state, ["exitCode"] = exitCode, ["at"] = At(at) });

    // ---- administrator checks (m9 §17.2): <id>.read.json before the first check, <id>.read-end.json after the last.
    // Neither matches the host's "rcpt-*.session.json" enumeration (BrokerJournalFiles.Read).

    public string BeginRead(BrokerReadRequest request, DateTimeOffset at)
    {
        var id = BrokerProtocol.NewReceiptId();
        Create(id + ".read.json", new JsonObject
        {
            ["receiptId"] = id, ["requestId"] = request.RequestId, ["at"] = At(at), ["userSid"] = user.Value,
            ["checks"] = new JsonArray([.. request.Checks.Select(c => (JsonNode?)new JsonObject { ["n"] = c.N, ["id"] = c.Id, ["handlerVersion"] = c.HandlerVersion, ["window"] = c.Window })]),
        });
        return id;
    }

    public void EndRead(string receiptId, string state, IReadOnlyList<(int N, string State, string? Code)> results, DateTimeOffset at) =>
        Create(receiptId + ".read-end.json", new JsonObject
        {
            ["receiptId"] = receiptId, ["state"] = state, ["at"] = At(at),
            ["checks"] = new JsonArray([.. results.Select(x => (JsonNode?)new JsonObject { ["n"] = x.N, ["state"] = x.State, ["code"] = x.Code })]),
        });

    private static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public string Begin(BrokerRequest request, DateTimeOffset at)
    {
        var id = BrokerProtocol.NewReceiptId();
        Create(BrokerJournalFiles.SessionFile(id), new JsonObject
        {
            ["receiptId"] = id, ["planId"] = request.PlanId, ["planHash"] = request.PlanHash, ["at"] = At(at), ["userSid"] = user.Value,
            ["steps"] = new JsonArray([.. request.Steps.Select(s => (JsonNode?)new JsonObject { ["n"] = s.N, ["actionId"] = s.ActionId })]),
        });
        return id;
    }

    public void Prepared(string receiptId, BrokerStepRequest step, ActionResolution r, DateTimeOffset at) =>
        Create(BrokerJournalFiles.PreparedFile(receiptId, step.N), new JsonObject
        {
            ["receiptId"] = receiptId, ["n"] = step.N, ["actionId"] = step.ActionId, ["contentHash"] = step.ContentHash,
            ["target"] = r.Target.DeepClone(), ["before"] = r.Before.DeepClone(), ["after"] = r.After.DeepClone(), ["at"] = At(at),
        });

    public void Outcome(string receiptId, int n, ActionOutcome outcome, DateTimeOffset at) =>
        Create(BrokerJournalFiles.OutcomeFile(receiptId, n), new JsonObject
        {
            ["receiptId"] = receiptId, ["n"] = n, ["state"] = outcome.State, ["observed"] = outcome.Observed.DeepClone(), ["code"] = outcome.Code, ["at"] = At(at),
        });

    public BrokerRecordStep? Find(string receiptId, int n) => BrokerJournalFiles.Find(root, receiptId, n, Key(), Reject);

    private void Create(string name, JsonObject record)
    {
        var bytes = BrokerJournalFiles.Seal(name, record, Key());
        using var fs = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>The trusted folder (held) and the broker's key.</summary>
    internal byte[] Key()
    {
        EnsureDirectory();
        return _key!;
    }

    /// <summary>Also the home of the nonce ledger: the chain is opened (and on first use the folder quarantined) before anything in it is read.</summary>
    public void EnsureDirectory()
    {
        lock (_gate)
        {
            _chain ??= TrustedChain.Open(_files, trustRoot, root);
            _key ??= LoadOrProvisionKey();
        }
    }

    private byte[] LoadOrProvisionKey()
    {
        var path = Path.Combine(root, BrokerJournalFiles.KeyFile);
        // m9 review S3b: under the journal lock, so brokers provisioning at once end with one key. Released on return,
        // before anything (the nonce ledger) takes it again.
        using var held = JournalLock.Hold(_files, Path.Combine(root, FileNonceLedger.LockName), _lockTimeout, _time, _sleep)
            ?? throw new IOException("The journal lock is held by another broker.");
        if (!TrustPolicy.IsSecret(held.Facts)) throw new IOException("The journal lock is not the broker's own.");
        // One handle: validated, then read. A path swap after the open can't change what is used. Read under the lock, so a
        // key another broker created a moment ago is found and adopted here.
        if (_files.ReadSecret(path, 32) is { TooLong: false } read && TrustPolicy.IsSecret(read.Facts) && read.Bytes.Length == 32) return read.Bytes;
        // No key the broker can prove is its own: this is the first provisioning, and nothing already here is adopted.
        Quarantine();
        var key = RandomNumberGenerator.GetBytes(32);
        using (var fs = _files.CreateSecret(path))
        {
            fs.Write(key);
            fs.Flush(flushToDisk: true);
        }
        return key;
    }

    /// <summary>Moves every entry in the folder (except earlier quarantine folders and the journal lock, which the caller
    /// holds), or only <paramref name="only"/>, into a new quarantine subfolder.</summary>
    internal void Quarantine(string? only = null)
    {
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Where(e => !Path.GetFileName(e).StartsWith(BrokerJournalFiles.QuarantinePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => !string.Equals(Path.GetFileName(e), FileNonceLedger.LockName, StringComparison.OrdinalIgnoreCase))
            .Where(e => only is null || string.Equals(Path.GetFileName(e), only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0) return;
        var q = Path.Combine(root, BrokerJournalFiles.QuarantinePrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        Directory.CreateDirectory(q);
        foreach (var e in entries)
        {
            var to = Path.Combine(q, Path.GetFileName(e));
            if (Directory.Exists(e)) Directory.Move(e, to); // a junction or link is moved itself, never followed
            else File.Move(e, to);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _chain?.Dispose();
            _chain = null;
        }
    }
}

/// <summary>
/// The broker's run folders for elevated scripts (m9 §5.4): %ProgramData%\OptimizationTool\script-runs. The root (and
/// <c>parse</c> below it) is a <see cref="TrustedChain"/> like the journal's: created with its security at creation, or
/// validated through a held handle, an untrusted one retired and replaced, never re-secured; it stays held until the run
/// ends. Each run gets a new random folder inside it (inheriting its security), and ScriptStage still refuses a reparse
/// point anywhere on the path. Nothing already in it is ever read or adopted.
/// </summary>
internal static class BrokerRunFolders
{
    public static string Root => Path.Combine(TrustedChain.ProgramData, "OptimizationTool", "script-runs");

    /// <summary>For ScriptStage: opens the trusted chain to the given runs root and holds it until disposed.</summary>
    public static Func<string, IDisposable> Trusted(ITrustedFileSystem files, string trustRoot) => path => TrustedChain.Open(files, trustRoot, path);
}

/// <summary>
/// Hashes of used nonces (the last 1000), in <c>nonces.log</c> in the trusted journal folder (m9 §13 R1). The ledger is a
/// secret file (SYSTEM and Administrators only, security applied at creation, like the key). Its last line is
/// <c>mac:&lt;hex&gt;</c>: HMAC-SHA256 with the journal key over the file name, a newline and the hash lines joined by newlines.
/// It is read through one handle that is validated first, and written by creating a new secret temp file, writing and
/// flushing it, then renaming it over the ledger. A missing ledger is empty.
/// Rule: if the ledger exists but is unsigned, tampered, malformed, too long, not secret or a reparse point, the current
/// request is refused (<see cref="NonceConsume.Failed"/>), the bad ledger is moved into a <c>quarantine-*</c> folder and a
/// fresh signed ledger is started.
/// Atomic across brokers (m9 review S3): <see cref="TryConsume"/> is one step (load, membership check, append, flush, atomic
/// replace) under <see cref="LockName"/>, a secret lock file in the same folder held open with no sharing at all. When it
/// can't be taken within <see cref="LockTimeout"/> (another broker holds it), or it is not a secret, or anything in the
/// step fails, the request is refused and nothing runs.
/// </summary>
internal sealed class FileNonceLedger(ProtectedJournal journal, TimeSpan? lockTimeout = null, TimeProvider? time = null, Action<TimeSpan>? sleep = null) : INonceLedger
{
    public const string FileName = "nonces.log";
    public const string LockName = "nonces.lock";
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);
    private const int Keep = 1000, MaxBytes = 128 * 1024;
    private const string MacPrefix = "mac:";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;

    public TimeSpan LockTimeout { get; } = lockTimeout ?? DefaultLockTimeout;

    public NonceConsume TryConsume(string nonce)
    {
        try
        {
            journal.EnsureDirectory(); // the chain is held and the key loaded before anything in the folder is opened
            using var held = Lock();
            if (held is null) return NonceConsume.Failed; // another broker kept it past the timeout: fail closed
            if (!TrustPolicy.IsSecret(held.Facts)) return NonceConsume.Failed; // not the broker's own lock: refuse, write nothing
            if (Load() is not { } hashes)
            {
                Reset();
                return NonceConsume.Failed;
            }
            var hash = Hash(nonce);
            if (hashes.Contains(hash)) return NonceConsume.Replayed;
            hashes.Add(hash);
            Write(hashes.TakeLast(Keep).ToList());
            return NonceConsume.Consumed;
        }
        catch (Exception)
        {
            return NonceConsume.Failed;
        }
    }

    private IHeldLock? Lock() => JournalLock.Hold(journal.Files, Path.Combine(journal.Root, LockName), LockTimeout, _time, _sleep);

    /// <summary>The ledger's hashes; empty when missing; null when it exists but can't be trusted.</summary>
    private List<string>? Load()
    {
        var key = journal.Key(); // the chain is held and the key loaded before the ledger is read
        var read = journal.Files.ReadSecret(Path.Combine(journal.Root, FileName), MaxBytes);
        if (read is null) return [];
        if (read.TooLong || !TrustPolicy.IsSecret(read.Facts)) return null;
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(read.Bytes); }
        catch (DecoderFallbackException) { return null; }
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0 || !lines[^1].StartsWith(MacPrefix, StringComparison.Ordinal)) return null;
        var hashes = lines.Take(lines.Count - 1).ToList();
        if (hashes.Count > Keep || hashes.Any(h => h.Length != 64 || !h.All(char.IsAsciiHexDigitLower))) return null;
        byte[] claimed;
        try { claimed = Convert.FromHexString(lines[^1][MacPrefix.Length..]); }
        catch (FormatException) { return null; }
        return CryptographicOperations.FixedTimeEquals(claimed, Mac(key, hashes)) ? hashes : null;
    }

    private void Reset()
    {
        journal.Quarantine(FileName);
        Write([]);
    }

    private void Write(List<string> hashes)
    {
        var body = new StringBuilder();
        foreach (var h in hashes) body.Append(h).Append('\n');
        body.Append(MacPrefix).Append(Convert.ToHexStringLower(Mac(journal.Key(), hashes))).Append('\n');
        var temp = Path.Combine(journal.Root, FileName + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        using (var fs = journal.Files.CreateSecret(temp))
        {
            fs.Write(Encoding.UTF8.GetBytes(body.ToString()));
            fs.Flush(flushToDisk: true);
        }
        journal.Files.ReplaceSecret(temp, Path.Combine(journal.Root, FileName));
    }

    private static byte[] Mac(byte[] key, List<string> hashes) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(FileName + "\n" + string.Join('\n', hashes)));

    private static string Hash(string nonce) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(nonce)));
}

/// <summary>The journal lock (m9 review S3, S3b), retried every 25 ms until the timeout; null when another broker kept it.
/// Not re-entrant: a holder never asks for it again before releasing it.</summary>
internal static class JournalLock
{
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(25);

    public static IHeldLock? Hold(ITrustedFileSystem files, string path, TimeSpan timeout, TimeProvider time, Action<TimeSpan> sleep)
    {
        var start = time.GetTimestamp();
        while (true)
        {
            if (files.TryHoldLock(path) is { } held) return held;
            if (time.GetElapsedTime(start) >= timeout) return null;
            sleep(Retry);
        }
    }
}

/// <summary>Identity of this process and of the pipe server.</summary>
internal static class PeerIdentity
{
    public static PeerInfo Self()
    {
        using var id = WindowsIdentity.GetCurrent();
        ProcessIdToSessionId((uint)Environment.ProcessId, out var session);
        return new PeerInfo((uint)Environment.ProcessId, Environment.ProcessPath, session, id.User);
    }

    public static PeerInfo Server(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return new PeerInfo(0, null, uint.MaxValue, null);
        if (!ProcessIdToSessionId(pid, out var session)) session = uint.MaxValue;
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid) return new PeerInfo(pid, null, session, null);
        SecurityIdentifier? user = null;
        if (OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token))
        {
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                user = identity.User;
        }
        string? image = null;
        var buffer = new char[1024];
        var size = buffer.Length;
        if (QueryFullProcessImageNameW(process, 0, buffer, ref size)) image = new string(buffer, 0, size);
        return new PeerInfo(pid, image, session, user);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);
}
