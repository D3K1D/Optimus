using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Inventory;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Collectors;

/// <summary>
/// One autostart entry as read. <see cref="ImagePath"/> and <see cref="PayloadPath"/> are full local paths (only
/// ever normalized before they become evidence). Arguments are never kept — only <see cref="HasArguments"/>.
/// Source: run | runonce | folder | task | service. Location: hkcu | hklm | user | common | task folder | services.
/// LocalKey (run, runonce, folder): the exact registry value name or Startup-folder file name; evidence keeps only its
/// hash (entryKey, local) so a startup action can find the entry again.
/// </summary>
internal sealed record StartupItemRaw(
    string Source, string Location, string? View, string Name, string? ValueType,
    string? ImagePath, bool HasArguments, string? PayloadPath, SignatureInfo Signature, bool? Enabled, string? Trigger,
    string? LocalKey = null, global::OptimizationTool.Windows.Inventory.FileVersionFacts? File = null);

internal sealed record StartupRaw(
    IReadOnlyList<StartupItemRaw> Items, IReadOnlyList<string> SourceErrors,
    int TaskFoldersDenied, int TasksUnreadable, int MicrosoftTasksOmitted, int MicrosoftServicesOmitted, bool Truncated,
    int ServicesUnreadable = 0);

/// <summary>
/// win.security.startup — Run/RunOnce (HKCU; HKLM 64- and 32-bit views), Startup folders (user and common),
/// scheduled tasks with logon or boot triggers (Task Scheduler COM, read-only enumeration), and auto-start
/// services that are not Microsoft-signed. Launch strings are parsed as text only (see <see cref="LaunchString"/>);
/// nothing is executed. Signature status comes from WinVerifyTrust on the resolved image (payload for rundll32)
/// without network revocation. Microsoft-signed services and \Microsoft\ tasks are counted, not listed.
/// Task folders a standard user cannot open are counted and make coverage partial.
/// </summary>
internal sealed class StartupCollector(CollectorOptions options, PathRoots? roots = null)
    : SecurityCollector<StartupRaw>(options, Max(options.DefaultTimeout, TimeSpan.FromSeconds(60)))
{
    public const string CollectorId = "win.security.startup";
    internal const int ItemCap = 400;
    private const int TaskScanCap = 3000;
    private const int ServiceScanCap = 1500;
    private readonly PathRoots _roots = roots ?? PathRoots.FromEnvironment();

    public override string Id => CollectorId;
    public override string GapTitle => "Startup items";
    public override string GapSource => "Startup locations";
    public override string GapDisplay => "Startup items could not be read.";

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // ------------------------------------------------------------------ read

    internal override StartupRaw Read(CancellationToken ct) => ReadScope(_roots, inventory: false, ct);

    /// <summary>
    /// The read itself. <paramref name="inventory"/> (m9 §6.10, win.inventory.background) widens it to scheduled tasks with
    /// any trigger and to services of any start type except disabled; the Microsoft rules and caps are the same.
    /// </summary>
    internal static StartupRaw ReadScope(PathRoots roots, bool inventory, CancellationToken ct)
    {
        var ctx = new ReadContext(roots, inventory, ct);
        try
        {
            Try(ctx, "run keys", () => ReadRunKeys(ctx));
            Try(ctx, "startup folders", () => ReadStartupFolders(ctx));
            Try(ctx, "scheduled tasks", () => ReadTasks(ctx));
            Try(ctx, "services", () => ReadServices(ctx));
            return new StartupRaw(ctx.Items, ctx.Errors, ctx.TaskFoldersDenied, ctx.TasksUnreadable,
                ctx.MicrosoftTasks, ctx.MicrosoftServices, ctx.Truncated, ctx.ServicesUnreadable);
        }
        finally { ctx.Signatures.Dispose(); }
    }

    private sealed class ReadContext(PathRoots roots, bool inventory, CancellationToken ct)
    {
        private readonly Dictionary<string, global::OptimizationTool.Windows.Inventory.FileVersionFacts?> _files = new(StringComparer.OrdinalIgnoreCase);
        public PathRoots Roots { get; } = roots;
        public bool Inventory { get; } = inventory;
        public CancellationToken Ct { get; } = ct;
        public SignatureChecker Signatures { get; } = new();
        public List<StartupItemRaw> Items { get; } = [];
        public List<string> Errors { get; } = [];
        public string SystemDir { get; } = Environment.SystemDirectory;
        public int TaskFoldersDenied, TasksUnreadable, MicrosoftTasks, MicrosoftServices, ServicesUnreadable;
        public bool Truncated;

        public bool Add(StartupItemRaw item)
        {
            if (Items.Count >= ItemCap) { Truncated = true; return false; }
            Items.Add(item);
            return true;
        }

        public LaunchString.Parsed Parse(string? raw) => LaunchString.Parse(raw, Roots, File.Exists, SystemDir);

        public SignatureInfo Sign(LaunchString.Parsed p) => Signatures.Check(p.Payload ?? p.Image);

        /// <summary>The version resource (company, version, product) of what the entry launches, read once per file.</summary>
        public global::OptimizationTool.Windows.Inventory.FileVersionFacts? FileFacts(LaunchString.Parsed p)
        {
            var path = p.Payload ?? p.Image;
            if (path is null) return null;
            if (!_files.TryGetValue(path, out var facts)) _files[path] = facts = global::OptimizationTool.Windows.Inventory.FileVersions.Read(path);
            return facts;
        }
    }

    private static void Try(ReadContext ctx, string source, Action read)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        try { read(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ctx.Errors.Add($"{source}: {ex.GetType().Name}"); }
    }

    private static void ReadRunKeys(ReadContext ctx)
    {
        var sets = new (RegistryHive Hive, RegistryView View, string Loc, string ViewName, string Approved)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, "hkcu", "64", "Run"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, "hklm", "64", "Run"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "hklm", "32", "Run32"),
        };
        foreach (var (hive, view, loc, viewName, approvedName) in sets)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            var approved = Approved(root, approvedName);
            foreach (var (keyName, source) in new[] { ("Run", "run"), ("RunOnce", "runonce") })
            {
                ctx.Ct.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\" + keyName, writable: false);
                if (key is null) continue;
                foreach (var name in key.GetValueNames().Take(200))
                {
                    var kind = key.GetValueKind(name);
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    var parsed = ctx.Parse(raw);
                    bool? enabled = source == "run" && approved.TryGetValue(name, out var en) ? en : source == "run" ? true : null;
                    if (!ctx.Add(new StartupItemRaw(source, loc, viewName, name.Length == 0 ? "(Default)" : name, KindName(kind),
                        parsed.Image, parsed.HasArguments, parsed.Payload, ctx.Sign(parsed), enabled, "logon", LocalKey: name, File: ctx.FileFacts(parsed)))) return;
                }
            }
        }
    }

    /// <summary>Explorer\StartupApproved: first byte even = enabled, odd = disabled (Task Manager / Settings toggle).</summary>
    private static Dictionary<string, bool> Approved(RegistryKey root, string name)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + name, writable: false);
        if (key is null) return map;
        foreach (var v in key.GetValueNames().Take(500))
            if (key.GetValue(v) is byte[] { Length: > 0 } b) map[v] = (b[0] & 1) == 0;
        return map;
    }

    private static void ReadStartupFolders(ReadContext ctx)
    {
        foreach (var (folder, loc, hive) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "user", RegistryHive.CurrentUser),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "common", RegistryHive.LocalMachine),
        })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            var approved = Approved(root, "StartupFolder");
            foreach (var file in Directory.EnumerateFiles(folder).Take(200))
            {
                ctx.Ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                LaunchString.Parsed parsed;
                if (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var (target, hasArgs) = ReadShortcut(file);
                    var image = target is null ? null : ctx.Roots.ExpandKnown(target);
                    parsed = new LaunchString.Parsed(image, hasArgs, null);
                }
                else parsed = new LaunchString.Parsed(file, false, null);
                var enabled = approved.TryGetValue(fileName, out var en) ? en : true;
                if (!ctx.Add(new StartupItemRaw("folder", loc, null, Path.GetFileNameWithoutExtension(fileName), null,
                    parsed.Image, parsed.HasArguments, null, ctx.Sign(parsed), enabled, "logon", LocalKey: fileName, File: ctx.FileFacts(parsed)))) return;
            }
        }
    }

    /// <summary>Loads the .lnk read-only and reads its stored target path. Never calls IShellLink::Resolve.</summary>
    private static (string? Target, bool HasArgs) ReadShortcut(string path)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_ShellLink, throwOnError: true)!)!;
            if (((IPersistFile)link).Load(path, Native.STGM_READ) != 0) return (null, false);
            var sl = (IShellLinkW)link;
            var buf = new char[1024];
            var target = sl.GetPath(buf, buf.Length, IntPtr.Zero, Native.SLGP_RAWPATH) == 0 ? new string(buf).TrimEnd('\0') : null;
            var args = new char[64];
            var hasArgs = sl.GetArguments(args, args.Length) == 0 && args[0] != '\0';
            return (string.IsNullOrWhiteSpace(target) ? null : target, hasArgs);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException) { return (null, false); }
        finally { Automation.Release(link); }
    }

    private static void ReadTasks(ReadContext ctx)
    {
        object? service = null;
        try
        {
            service = Automation.Create("Schedule.Service");
            Automation.Call(service, "Connect");
            var root = Automation.Call(service, "GetFolder", "\\")!;
            var scanned = 0;
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0 && scanned < TaskScanCap)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                var folder = stack.Pop();
                try
                {
                    var folderPath = Automation.Get(folder, "Path") as string ?? "\\";
                    var microsoft = folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase);
                    object? tasks = null;
                    try { tasks = Automation.Call(folder, "GetTasks", 1 /* TASK_ENUM_HIDDEN */); }
                    catch (Exception ex) when (IsDenied(ex)) { ctx.TaskFoldersDenied++; }
                    if (tasks is not null)
                    {
                        foreach (var task in Automation.Items(tasks, TaskScanCap))
                        {
                            scanned++;
                            try { ReadTask(ctx, task, microsoft); }
                            finally { Automation.Release(task); }
                        }
                        Automation.Release(tasks);
                    }
                    object? subs = null;
                    try { subs = Automation.Call(folder, "GetFolders", 0); }
                    catch (Exception ex) when (IsDenied(ex)) { ctx.TaskFoldersDenied++; }
                    if (subs is not null)
                    {
                        foreach (var sub in Automation.Items(subs, 2000)) stack.Push(sub);
                        Automation.Release(subs);
                    }
                }
                finally { Automation.Release(folder); }
            }
            if (scanned >= TaskScanCap) ctx.Truncated = true;
            while (stack.Count > 0) Automation.Release(stack.Pop());
        }
        finally { Automation.Release(service); }
    }

    private static void ReadTask(ReadContext ctx, object task, bool microsoftFolder)
    {
        object? def = null;
        try
        {
            try { def = Automation.Get(task, "Definition"); }
            catch (Exception ex) when (IsDenied(ex) || ex is System.Reflection.TargetInvocationException) { ctx.TasksUnreadable++; return; }
            if (def is null) { ctx.TasksUnreadable++; return; }

            string? trigger = null;
            var triggers = Automation.Get(def, "Triggers")!;
            foreach (var t in Automation.Items(triggers, 64))
            {
                var type = Convert.ToInt32(Automation.Get(t, "Type"), CultureInfo.InvariantCulture);
                Automation.Release(t);
                if (type == 9) { trigger = "logon"; break; } // TASK_TRIGGER_LOGON
                if (type == 8) trigger ??= "boot";             // TASK_TRIGGER_BOOT
            }
            Automation.Release(triggers);
            if (trigger is null)
            {
                if (!ctx.Inventory) return;
                trigger = "schedule"; // m9 §6.10: the inventory lists every task, whatever starts it
            }

            string? execPath = null;
            var hasArgs = false;
            var actionKind = "none";
            var actions = Automation.Get(def, "Actions")!;
            foreach (var a in Automation.Items(actions, 32))
            {
                var type = Convert.ToInt32(Automation.Get(a, "Type"), CultureInfo.InvariantCulture);
                if (type == 0 && execPath is null) // TASK_ACTION_EXEC
                {
                    execPath = Automation.Get(a, "Path") as string;
                    hasArgs = !string.IsNullOrWhiteSpace(Automation.Get(a, "Arguments") as string);
                    actionKind = "exec";
                }
                else if (type == 5 && actionKind == "none") actionKind = "com_handler";
                Automation.Release(a);
            }
            Automation.Release(actions);

            var parsed = execPath is null ? new LaunchString.Parsed(null, false, null) : ctx.Parse(execPath);
            if (execPath is not null && hasArgs) parsed = parsed with { HasArguments = true };
            var sig = parsed.Image is null ? SignatureInfo.Unknown : ctx.Sign(parsed);
            if (microsoftFolder && (sig.IsMicrosoft || parsed.Image is null)) { ctx.MicrosoftTasks++; return; }

            var path = Automation.Get(task, "Path") as string ?? "";
            var enabled = Automation.Get(task, "Enabled") is bool b ? b : (bool?)null;
            var folder = path.LastIndexOf('\\') is var i and > 0 ? path[..i] : "\\";
            ctx.Add(new StartupItemRaw("task", folder, null, Automation.Get(task, "Name") as string ?? path, actionKind,
                parsed.Image, parsed.HasArguments, parsed.Payload, sig, enabled, trigger, LocalKey: path.Length > 0 ? path : null, File: ctx.FileFacts(parsed)));
        }
        finally { Automation.Release(def); }
    }

    private static void ReadServices(ReadContext ctx)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false);
        if (services is null) return;
        foreach (var name in services.GetSubKeyNames().Take(ServiceScanCap))
        {
            ctx.Ct.ThrowIfCancellationRequested();
            RegistryKey? key;
            try { key = services.OpenSubKey(name, writable: false); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { continue; }
            if (key is null) continue;
            using (key)
            {
                if (key.GetValue("Start") is not int start) continue;
                if (ctx.Inventory ? start is not (0 or 1 or 2 or 3) : start != 2) continue; // SERVICE_AUTO_START (the inventory: any but disabled)
                if (key.GetValue("Type") is not int type || (type & 0x30) == 0) continue;  // Win32 own/share process only
                var imagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (string.IsNullOrWhiteSpace(imagePath)) continue;
                var parsed = ctx.Parse(imagePath);
                if ((type & 0x20) != 0 && PathRoots.FileName(parsed.Image ?? "").Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string? dll = null;
                    try
                    {
                        using var p = key.OpenSubKey("Parameters", writable: false);
                        dll = p?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { ctx.ServicesUnreadable++; }
                    dll ??= key.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (dll is not null) parsed = parsed with { Payload = ctx.Parse(dll).Image };
                }
                var sig = ctx.Sign(parsed);
                if (sig.IsMicrosoft) { ctx.MicrosoftServices++; continue; }
                var display = key.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(display) || display.StartsWith('@')) display = name;
                if (!ctx.Add(new StartupItemRaw("service", "services", null, display, name,
                    parsed.Image, parsed.HasArguments, parsed.Payload, sig, true, start == 3 ? "manual" : "boot", LocalKey: name, File: ctx.FileFacts(parsed)))) return;
            }
        }
    }

    private static bool IsDenied(Exception ex) =>
        Automation.HResult(ex) == unchecked((int)0x80070005) || ex is UnauthorizedAccessException
        || ex.InnerException is UnauthorizedAccessException;

    private static string KindName(RegistryValueKind k) => k switch
    {
        RegistryValueKind.String => "REG_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.MultiString => "REG_MULTI_SZ",
        RegistryValueKind.Binary => "REG_BINARY",
        RegistryValueKind.DWord => "REG_DWORD",
        _ => "other",
    };

    // ------------------------------------------------------------------ normalize

    internal string? UserName { get; init; } = Environment.UserName;

    /// <summary>This PC's user, PC and domain names, which the stored company and version may not name (m9 review R4).</summary>
    internal IReadOnlyList<LocalIdentity> Identities { get => _identities ??= InventoryKit.LocalIdentities(); init => _identities = value; }
    private IReadOnlyList<LocalIdentity>? _identities;

    internal override CollectorResult Normalize(StartupRaw raw)
    {
        var facts = new FactSet("Startup locations");
        var bySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var signIn = 0;
        var signInEnabled = 0;
        var unsigned = 0;
        foreach (var item in raw.Items)
        {
            var image = _roots.Normalize(item.ImagePath);
            var payload = _roots.Normalize(item.PayloadPath);
            var name = Text.Redact(item.Name, UserName);
            if (name.Length == 0) name = "Unnamed entry";
            var location = item.Source == "task" ? Text.Redact(item.Location, UserName, 160) : item.Location;
            var identity = item.Source switch
            {
                "service" => item.ValueType ?? name,                       // service key name
                "task" => location + "\\" + item.Name,
                _ => string.Join('|', item.Location, item.View ?? "", item.Name),
            };
            var subjectLoc = item.Source is "run" or "runonce" or "folder" ? "." + item.Location : "";
            var subject = $"startup.{item.Source}{subjectLoc}.{Subjects.Hash8(identity)}";
            if (facts.Contains(subject)) continue;

            bySource[item.Source] = bySource.GetValueOrDefault(item.Source) + 1;
            var isSignIn = item.Source is "run" or "runonce" or "folder" || (item.Source == "task" && item.Trigger == "logon");
            if (isSignIn) { signIn++; if (item.Enabled != false) signInEnabled++; }
            if (item.Signature.Status == "unsigned") unsigned++;

            facts.Add(subject, new JsonObject
            {
                ["source"] = item.Source,
                ["location"] = location,
                ["view"] = item.View,
                ["valueType"] = item.Source == "service" ? null : item.ValueType,
                ["name"] = name,
                ["image"] = image,
                ["hasArguments"] = item.HasArguments,
                ["payload"] = payload,
                ["signature"] = item.Signature.Status,
                ["signer"] = item.Signature.Signer,
                ["catalogSigned"] = item.Signature.Catalog,
                ["enabled"] = item.Enabled,
                ["trigger"] = item.Trigger,
                // m9 §6.10: what the launched file says about itself (its version resource), unverified; the signer above is the verified publisher.
                // m9 review R4: the same never-store and identity check as an inventory item's publisher and version.
                ["company"] = InventoryKit.Clean(item.File?.Company, Identities, 80),
                ["version"] = InventoryKit.Version(item.File?.Version, Identities),
                ["signIn"] = isSignIn,
                // Local only (m9 §3.1): a SHA-256 key of the exact value name (run, runonce), file name (folder), full task
                // path (task) or service key name (service), so an action can find this entry again without the name (which
                // may hold the user's name) in evidence. The projector's allowlist never releases it.
                ["entryKey"] = item.LocalKey is not null ? global::OptimizationTool.Windows.Actions.Startup.StartupKeys.Of(item.LocalKey) : null,
            }, Display(item, name));
        }

        facts.Add("startup.summary", new JsonObject
        {
            ["total"] = bySource.Values.Sum(),
            ["signInCount"] = signIn,
            ["signInEnabledCount"] = signInEnabled,
            ["bySource"] = new JsonObject(bySource.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            ["unsignedCount"] = unsigned,
            ["microsoftServicesOmitted"] = raw.MicrosoftServicesOmitted,
            ["microsoftTasksOmitted"] = raw.MicrosoftTasksOmitted,
            ["taskFoldersDenied"] = raw.TaskFoldersDenied,
            ["tasksUnreadable"] = raw.TasksUnreadable,
            ["servicesUnreadable"] = raw.ServicesUnreadable,
            ["truncated"] = raw.Truncated,
        }, signInEnabled == 1 ? "1 app starts when you sign in." : $"{signInEnabled} apps start when you sign in.");

        var reasons = new List<string>(raw.SourceErrors);
        if (raw.TaskFoldersDenied > 0) reasons.Add($"{raw.TaskFoldersDenied} task folders need administrator rights.");
        if (raw.TasksUnreadable > 0) reasons.Add($"{raw.TasksUnreadable} tasks could not be read.");
        if (raw.ServicesUnreadable > 0) reasons.Add($"{raw.ServicesUnreadable} service settings need administrator rights.");
        if (raw.Truncated) reasons.Add($"Capped at {ItemCap} entries.");
        var status = raw.SourceErrors.Count >= 4 ? "failed" : reasons.Count == 0 ? "complete" : "partial";
        return facts.Result(status, reasons.Count == 0 ? null : string.Join(" ", reasons));
    }

    private static string Display(StartupItemRaw item, string name)
    {
        var where = item.Source switch
        {
            "run" => item.Location == "hkcu" ? "starts at sign-in for you (Run key)" : "starts at sign-in for everyone (Run key)",
            "runonce" => "runs once at next sign-in (RunOnce key)",
            "folder" => item.Location == "user" ? "starts at sign-in from your Startup folder" : "starts at sign-in from the shared Startup folder",
            "task" => item.Trigger == "logon" ? "is a scheduled task that runs at sign-in" : "is a scheduled task that runs at startup",
            _ => "is a service that starts automatically",
        };
        var sig = item.Signature.Status switch
        {
            "valid" when item.Signature.Signer is { } s => $" Signed by {s.TrimEnd('.')}.",
            "valid" => " Signed.",
            "unsigned" => " Not signed.",
            "invalid" => " Signature did not verify.",
            "not_found" => " Its file was not found.",
            _ => "",
        };
        var off = item.Enabled == false ? " Turned off in startup settings." : "";
        return $"{name} {where}.{sig}{off}";
    }
}
