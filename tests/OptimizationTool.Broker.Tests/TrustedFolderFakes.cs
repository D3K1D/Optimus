using System.Security.Principal;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// A fake <see cref="ITrustedFileSystem"/> over a temp folder: the files are real (plain file I/O only), their Windows
/// security is a table of facts here. Nothing sets an ACL or an owner on this PC. An entry with no facts is what an
/// unelevated user would have made: owned by the user, not protected, the user has full control.
/// One fake shared by several journals/ledgers models one disk seen by several broker processes: its state is guarded, and
/// a lock file held through <see cref="TryHoldLock"/> can't be held again (by anyone) until it is released.
/// </summary>
internal sealed class FakeTrustedFs : ITrustedFileSystem
{
    private const uint Full = 0x001F01FF;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, ObjectFacts> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> BlockRetire { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Created { get; } = [];
    public List<string> Retired { get; } = [];
    /// <summary>Runs after a secret's facts and bytes were taken from "the handle" (models a path swap after the open).</summary>
    public Action<string>? AfterSecretOpen { get; set; }
    /// <summary>Runs (outside the fake's guard) each time <see cref="TryHoldLock"/> finds the lock held by someone else.</summary>
    public Action<string>? LockBusy { get; set; }
    /// <summary>Runs (outside the fake's guard) just before a secret file is created.</summary>
    public Action<string>? BeforeCreateSecret { get; set; }

    public static ObjectFacts TrustedFolder => new(false, true, TrustPolicy.Admins, true,
        [new(TrustPolicy.SystemSid, Full, AceKind.Allow), new(TrustPolicy.Admins, Full, AceKind.Allow), new(Kit.User, 0x001200A9, AceKind.Allow)]);
    public static ObjectFacts SystemFolder => new(false, true, TrustPolicy.TrustedInstaller, false,
        [new(TrustPolicy.SystemSid, Full, AceKind.Allow), new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), 0x001200A9 | 0x4 /* create folders */, AceKind.Allow)]);
    public static ObjectFacts UserFolder => new(false, true, Kit.User, false, [new(Kit.User, Full, AceKind.Allow)]);
    public static ObjectFacts SecretFile => new(false, false, TrustPolicy.Admins, true,
        [new(TrustPolicy.SystemSid, Full, AceKind.Allow), new(TrustPolicy.Admins, Full, AceKind.Allow)]);
    public static ObjectFacts UserFile => new(false, false, Kit.User, false, [new(Kit.User, Full, AceKind.Allow)]);

    private static string K(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public void Set(string path, ObjectFacts facts) { lock (_sync) _facts[K(path)] = facts; }
    public ObjectFacts? FactsOf(string path) { lock (_sync) return _facts.TryGetValue(K(path), out var f) ? f : null; }
    public bool IsHeld(string path) { lock (_sync) return _held.TryGetValue(K(path), out var n) && n > 0; }
    public bool IsLocked(string path) { lock (_sync) return _locked.Contains(K(path)); }
    public bool IsSecret(string path) => FactsOf(path) is { } f && TrustPolicy.IsSecret(f);

    private ObjectFacts Of(string path)
    {
        var dir = Directory.Exists(path);
        return _facts.TryGetValue(K(path), out var f) ? f with { IsDirectory = dir } : dir ? UserFolder : UserFile;
    }

    public IHeldFolder? OpenFolder(string path)
    {
        lock (_sync)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return null;
            _held[K(path)] = _held.GetValueOrDefault(K(path)) + 1;
            return new Held(this, path, Of(path));
        }
    }

    public IHeldFolder CreateFolder(string path)
    {
        lock (_sync)
        {
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Cannot create a file when that file already exists.");
            Directory.CreateDirectory(path);
            Created.Add(K(path));
            _facts[K(path)] = TrustedFolder;
        }
        return OpenFolder(path)!;
    }

    public void Retire(string path, string newName)
    {
        lock (_sync)
        {
            if (BlockRetire.Contains(K(path)) || (_held.TryGetValue(K(path), out var n) && n > 0))
                throw new IOException("The process cannot access the file because it is being used by another process.");
            var to = Path.Combine(Path.GetDirectoryName(K(path))!, newName);
            if (Directory.Exists(path)) Directory.Move(path, to);
            else File.Move(path, to);
            foreach (var k in _facts.Keys.Where(k => k.Equals(K(path), StringComparison.OrdinalIgnoreCase) || k.StartsWith(K(path) + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _facts[to + k[K(path).Length..]] = _facts[k];
                _facts.Remove(k);
            }
            Retired.Add(to);
        }
    }

    public SecretRead? ReadSecret(string path, int maxBytes)
    {
        ObjectFacts facts;
        byte[] bytes;
        lock (_sync)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return null;
            facts = Of(path);
            if (facts.IsReparsePoint || facts.IsDirectory) return new SecretRead(facts, [], false);
            bytes = File.ReadAllBytes(path); // the snapshot "the handle" read
        }
        AfterSecretOpen?.Invoke(path);
        return bytes.Length > maxBytes ? new SecretRead(facts, [], true) : new SecretRead(facts, bytes, false);
    }

    public FileStream CreateSecret(string path)
    {
        BeforeCreateSecret?.Invoke(path);
        lock (_sync)
        {
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _facts[K(path)] = SecretFile;
            return fs;
        }
    }

    public void ReplaceSecret(string source, string path)
    {
        lock (_sync)
        {
            File.Move(source, path, overwrite: true);
            _facts[K(path)] = Of(source);
            _facts.Remove(K(source));
        }
    }

    public IHeldLock? TryHoldLock(string path)
    {
        lock (_sync)
        {
            if (!_locked.Contains(K(path)))
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    _facts[K(path)] = SecretFile; // created with the secret's security
                }
                _locked.Add(K(path));
                return new HeldLock(this, path, Of(path));
            }
        }
        LockBusy?.Invoke(path);
        return null;
    }

    private sealed class Held(FakeTrustedFs owner, string path, ObjectFacts facts) : IHeldFolder
    {
        private bool _done;
        public string Path { get; } = path;
        public ObjectFacts Facts { get; } = facts;
        public void Dispose()
        {
            lock (owner._sync)
            {
                if (_done) return;
                _done = true;
                owner._held[K(Path)]--;
            }
        }
    }

    private sealed class HeldLock(FakeTrustedFs owner, string path, ObjectFacts facts) : IHeldLock
    {
        private bool _done;
        public ObjectFacts Facts { get; } = facts;
        public void Dispose()
        {
            lock (owner._sync)
            {
                if (_done) return;
                _done = true;
                owner._locked.Remove(K(path));
            }
        }
    }
}
