using System.Diagnostics;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Inventory;

/// <summary>One running program as read: its process name, the image it runs (a full local path, never stored as is), its signature and version resource, and how many processes run it.</summary>
internal sealed record ProcessImageRaw(string Name, string? ImagePath, SignatureInfo Signature, FileVersionFacts? File, int Count);

internal sealed record BackgroundRaw(StartupRaw? Startup, string? StartupStatus, IReadOnlyList<ProcessImageRaw>? Processes, string? ProcessStatus,
    int ProcessesUnreadable, int ProcessImagesOmitted);

/// <summary>
/// win.inventory.background (m9 §6.10): what runs in the background. Startup items, services and scheduled tasks come from
/// the startup collector's own reader in its inventory scope (tasks with any trigger, services of any start type but
/// disabled; Microsoft-signed services and \Microsoft\ tasks are counted, not listed); running processes are the full
/// list (bounded), one entry per program image. Each entry: its name, the publisher of what it launches (the verified
/// signer, or the file's CompanyName marked unverified) and the file's version. Image paths are kept only in the local
/// normalized form (%ProgramFiles%\…), as the startup evidence keeps them; arguments, accounts and PIDs are never kept.
/// </summary>
internal sealed class BackgroundInventoryCollector(Func<CancellationToken, BackgroundRaw>? read = null, IReadOnlyList<LocalIdentity>? identities = null, PathRoots? roots = null)
    : InventoryCollector<BackgroundRaw>(read ?? (ct => BackgroundInventoryReader.Read(roots ?? PathRoots.FromEnvironment(), ct)), identities)
{
    public const int Cap = 500;
    private readonly PathRoots _roots = roots ?? PathRoots.FromEnvironment();

    public override string Area => "background";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(75);
    public override string GapTitle => "Background apps list";
    protected override bool Sta => true;

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["startup"] = "Startup items, services and tasks", ["processes"] = "Running programs",
    };

    internal override CollectorResult Normalize(BackgroundRaw raw)
    {
        var ids = Identities;
        var items = new ItemList(Cap);
        var status = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["startup"] = raw.StartupStatus ?? (raw.Startup is null ? "failed" : "ok"),
            ["processes"] = raw.ProcessStatus ?? (raw.Processes is null ? "failed" : "ok"),
        };
        var more = new List<string>();

        if (raw.Startup is { } s)
        {
            foreach (var item in s.Items)
            {
                var name = InventoryKit.Clean(item.Name, ids);
                if (name is null) continue;
                var (publisher, verified) = Publisher(item.Signature, item.File, ids);
                items.Add(InventoryKit.Item(("source", item.Source), ("name", name), ("publisher", publisher), ("publisherVerified", publisher is null ? null : verified),
                    ("version", InventoryKit.Version(item.File?.Version, ids)), ("signature", item.Signature.Status), ("trigger", item.Trigger),
                    ("enabled", item.Enabled), ("image", _roots.Normalize(item.PayloadPath ?? item.ImagePath))));
            }
            if (s.TaskFoldersDenied > 0) more.Add($"{s.TaskFoldersDenied} task folders need administrator rights.");
            if (s.TasksUnreadable > 0) more.Add($"{s.TasksUnreadable} tasks could not be read.");
            if (s.ServicesUnreadable > 0) more.Add($"{s.ServicesUnreadable} service settings need administrator rights.");
            if (s.Truncated) more.Add($"Startup items, services and tasks were capped at {StartupCollector.ItemCap}.");
            if (s.SourceErrors.Count > 0) more.Add($"{s.SourceErrors.Count} startup sources could not be read.");
        }

        foreach (var p in raw.Processes ?? [])
        {
            // The program's own name when its file says one, else the image's file name.
            var fileName = p.ImagePath is null ? p.Name : PathRoots.FileName(PathRoots.StripPrefixes(p.ImagePath));
            var name = InventoryKit.Clean(p.File?.Product, ids) ?? InventoryKit.Clean(fileName, ids);
            if (name is null) continue;
            var (publisher, verified) = Publisher(p.Signature, p.File, ids);
            items.Add(InventoryKit.Item(("source", "process"), ("name", name), ("publisher", publisher), ("publisherVerified", publisher is null ? null : verified),
                ("version", InventoryKit.Version(p.File?.Version, ids)), ("signature", p.Signature.Status), ("file", InventoryKit.Clean(fileName, ids, 120)),
                ("image", _roots.Normalize(p.ImagePath)), ("count", p.Count > 1 ? p.Count : null)));
        }
        if (raw.ProcessesUnreadable > 0) more.Add($"{raw.ProcessesUnreadable} running programs could not be opened without administrator rights.");
        if (raw.ProcessImagesOmitted > 0) more.Add($"{raw.ProcessImagesOmitted} more running programs were not read.");

        var counts = new System.Text.Json.Nodes.JsonObject
        {
            ["microsoftServicesOmitted"] = raw.Startup?.MicrosoftServicesOmitted ?? 0,
            ["microsoftTasksOmitted"] = raw.Startup?.MicrosoftTasksOmitted ?? 0,
            ["processesUnreadable"] = raw.ProcessesUnreadable,
        };
        var fact = InventoryKit.Fact(Area, "Startup locations and running programs", items, status,
            $"{InventoryKit.Plural(items.Total, "background item", "background items")} listed.", counts);
        // Unreadable details of single programs are counted in the value, not a gap: the program is still listed.
        var gaps = more.Where(m => !m.Contains("could not be opened", StringComparison.Ordinal)).ToList();
        return InventoryKit.Result(fact, status, Labels, items, gaps);
    }

    /// <summary>The verified signer, else the file's CompanyName (unverified), else nothing.</summary>
    private static (string? Publisher, bool Verified) Publisher(SignatureInfo sig, FileVersionFacts? file, IReadOnlyList<LocalIdentity> ids)
    {
        if (sig.Status == "valid" && InventoryKit.Clean(sig.Signer, ids, 80) is { } signer) return (signer, true);
        return (InventoryKit.Clean(file?.Company, ids, 80), false);
    }
}

/// <summary>Windows calls only (read-only, standard user): the startup reader and a limited-information look at each running process.</summary>
internal static class BackgroundInventoryReader
{
    internal const int ImageCap = 400;
    private static readonly string[] NoImage = ["Idle", "System", "Registry", "Memory Compression", "Secure System", "Interrupts", "vmmem"];

    public static BackgroundRaw Read(PathRoots roots, CancellationToken ct)
    {
        StartupRaw? startup = null;
        string? startupStatus = null;
        try { startup = StartupCollector.ReadScope(roots, inventory: true, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { startupStatus = InventoryKit.StatusOf(ex); }

        List<ProcessImageRaw>? processes = null;
        string? processStatus = null;
        int unreadable = 0, omitted = 0;
        try { (processes, unreadable, omitted) = ReadProcesses(new WindowsProcessControl(), ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { processStatus = InventoryKit.StatusOf(ex); }
        return new BackgroundRaw(startup, startupStatus, processes, processStatus, unreadable, omitted);
    }

    private static (List<ProcessImageRaw>, int Unreadable, int Omitted) ReadProcesses(IProcessControl control, CancellationToken ct)
    {
        var byImage = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        var self = (uint)Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                ct.ThrowIfCancellationRequested();
                uint pid;
                string name;
                try { pid = (uint)p.Id; name = p.ProcessName; }
                catch (InvalidOperationException) { continue; } // exited
                if (pid is 0 or 4 || pid == self || NoImage.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("OptimizationTool", StringComparison.OrdinalIgnoreCase)) continue; // this app
                var image = control.Query(pid)?.ImagePath;
                if (image is null) { unreadable++; continue; }
                byImage[image] = byImage.TryGetValue(image, out var hit) ? (hit.Name, hit.Count + 1) : (name, 1);
            }
        }
        var list = new List<ProcessImageRaw>();
        using var signatures = new SignatureChecker();
        foreach (var (image, (name, count)) in byImage.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(ImageCap))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new ProcessImageRaw(name, image, signatures.Check(image), FileVersions.Read(image), count));
        }
        return (list, unreadable, Math.Max(0, byImage.Count - ImageCap));
    }
}
