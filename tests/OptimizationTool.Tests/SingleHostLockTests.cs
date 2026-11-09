using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using OptimizationTool.Engine;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>
/// One host per user and data folder across Windows sessions (protocol §1): a second acquire fails while the first
/// is held, a host that dies never blocks the next one, and the real host exe exits with "other session".
/// A mutex belongs to a thread, so each holder here is its own thread (as each host is its own process).
/// </summary>
public class SingleHostLockTests
{
    private static string NewName() => SingleHostLock.NameForCurrentUser(TestHost.NewDataDir());

    /// <summary>A thread that takes the lock, then keeps it until told to release it or to end without releasing.</summary>
    private sealed class Holder
    {
        private readonly ManualResetEventSlim _go = new();
        private readonly TaskCompletionSource<bool> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private bool _release;
        private SingleHostLock? _lock; // kept alive on purpose when the thread ends without releasing (an abandoned mutex)

        public Holder(string name)
        {
            _thread = new Thread(() =>
            {
                _lock = SingleHostLock.TryAcquire(name);
                _acquired.SetResult(_lock is not null);
                if (_lock is null) return;
                _go.Wait();
                if (_release) _lock.Dispose();
            }) { IsBackground = true };
            _thread.Start();
        }

        public Task<bool> Acquired => _acquired.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Release() { _release = true; _go.Set(); _thread.Join(); }
        /// <summary>Ends the owning thread with the mutex still held, as a crashed or killed host leaves it.</summary>
        public void Die() { _go.Set(); _thread.Join(); }
    }

    [Fact]
    public async Task SecondAcquire_FailsWhileTheFirstHolds_AndWorksAfterRelease()
    {
        var name = NewName();
        var first = new Holder(name);
        Assert.True(await first.Acquired);
        Assert.False(await new Holder(name).Acquired);  // never waits: the second host is told at once
        first.Release();
        var third = new Holder(name);
        Assert.True(await third.Acquired);
        third.Release();
    }

    [Fact]
    public async Task AbandonedLock_IsTakenOver_ADeadHostNeverBlocksTheNext()
    {
        var name = NewName();
        var dead = new Holder(name);
        Assert.True(await dead.Acquired);
        dead.Die(); // the owner is gone without releasing; its handle is still open, so the mutex is abandoned, not gone
        var next = new Holder(name);
        Assert.True(await next.Acquired); // AbandonedMutexException means "it is ours now"
        Assert.False(await new Holder(name).Acquired); // and it is really held again
        next.Release();
        GC.KeepAlive(dead);
    }

    [Fact]
    public void Name_IsPerUserAndPerDataFolder_InTheGlobalNamespace()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var name = SingleHostLock.NameForCurrentUser(@"C:\Users\someone\AppData\Local\OptimizationTool");
        Assert.StartsWith(@"Global\OptimizationTool.Host." + sid + ".", name); // every session of the PC sees it
        Assert.Matches(@"\.[0-9a-f]{16}$", name);
        // The installed app and a dev build use the same default folder, however it is spelled: one lock.
        Assert.Equal(name, SingleHostLock.NameForCurrentUser(@"c:\users\SOMEONE\appdata\local\optimizationtool\"));
        // Another data folder (a dev host with --data-dir) or another user is another lock.
        Assert.NotEqual(name, SingleHostLock.NameForCurrentUser(@"C:\dev\data"));
        Assert.NotEqual(name, SingleHostLock.NameFor("S-1-5-21-1-2-3-1001", @"C:\Users\someone\AppData\Local\OptimizationTool"));
    }

    [Fact]
    public void Security_NamesTheUser_SoElevatedAndNotElevatedBothOpenIt()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var rules = SingleHostLock.CreateSecurity(user).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<MutexAccessRule>().ToList();
        // An elevated process's default ACL names Administrators, not the user: the explicit user rule is what lets
        // the same user's other elevation level open the mutex.
        Assert.Contains(rules, r => r.IdentityReference.Equals(user) && r.AccessControlType == AccessControlType.Allow && r.MutexRights == MutexRights.FullControl);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Equal(2, rules.Count); // the user and SYSTEM, nobody else
    }

    // ---- the real host exe ------------------------------------------------------------------------------------

    private static Process StartHost(string dataDir)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Host.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "--data-dir", dataDir, "--pipe-name", "OptimizationTool.test." + Guid.NewGuid().ToString("N"), "--source", "fixture", "--idle-exit-seconds", "0" })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string?> FirstLine(Process p) =>
        await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));

    [Fact]
    public async Task HostProcess_OnADatabaseFromANewerBuild_ExitsAsNeedsUpdate_WithOneLogLineAndNoStackTrace()
    {
        var dataDir = TestHost.NewDataDir();
        try
        {
            using (Store.Open(dataDir)) { }
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dataDir, Store.FileName)};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA user_version = {Migrations.Latest + 1}";
                cmd.ExecuteNonQuery();
            }

            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Host.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "--data-dir", dataDir, "--pipe-name", "OptimizationTool.test." + Guid.NewGuid().ToString("N"), "--source", "fixture", "--idle-exit-seconds", "0" })
                psi.ArgumentList.Add(a);
            using var host = Process.Start(psi)!;
            var stderr = host.StandardError.ReadToEndAsync();
            var stdout = host.StandardOutput.ReadToEndAsync();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(HostExitCodes.NeedsUpdate, host.ExitCode);
            var err = (await stderr).Trim();
            Assert.Contains($"needs_update: database schema v{Migrations.Latest + 1} is newer than this build supports (v{Migrations.Latest})", err);
            Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.DoesNotContain("   at ", err); // no stack trace
            Assert.DoesNotContain("ready ", await stdout);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task SecondHostProcess_OnTheSameDataFolder_ExitsAsOtherSession_AndAKilledHostDoesNotBlockTheNext()
    {
        var dataDir = TestHost.NewDataDir();
        Process? first = null, third = null;
        try
        {
            first = StartHost(dataDir);
            Assert.StartsWith("ready ", await FirstLine(first));

            // Another pipe name stands for another Windows session: same user, same database.
            using (var second = StartHost(dataDir))
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Equal(HostExitCodes.OtherSession, second.ExitCode);
            }

            first.Kill(entireProcessTree: true); // a crash: nothing is released in an orderly way
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            third = StartHost(dataDir);
            Assert.StartsWith("ready ", await FirstLine(third));
        }
        finally
        {
            foreach (var p in new[] { first, third })
            {
                if (p is null) continue;
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.Dispose();
            }
            try { Directory.Delete(dataDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
