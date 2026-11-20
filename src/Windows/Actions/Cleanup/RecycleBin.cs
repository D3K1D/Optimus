using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Cleanup;

/// <summary>The current user's Recycle Bin on every drive: item count and total size in bytes.</summary>
internal sealed record RecycleBinInfo(long Items, long Bytes);

/// <summary>
/// cleanup.empty_recycle_bin's seam. <see cref="Query"/> reads (SHQueryRecycleBin); <see cref="Empty"/> is the only write
/// (SHEmptyRecycleBin without confirmation, progress or sound) and returns its HRESULT. Tests use a fake; no test may call
/// the real Empty.
/// </summary>
internal interface IRecycleBin
{
    /// <summary>The bin now, or null when Windows didn't answer.</summary>
    RecycleBinInfo? Query();
    int Empty();
}

/// <summary>Real implementation (shell32). Written, never run by the tests.</summary>
internal sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long TotalBytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(nint hwnd, string? rootPath, uint flags);

    public RecycleBinInfo? Query()
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) < 0 ? null : new RecycleBinInfo(info.Items, info.TotalBytes);
    }

    public int Empty() => SHEmptyRecycleBin(0, null, NoConfirmation | NoProgressUi | NoSound);
}

/// <summary>
/// cleanup.empty_recycle_bin (contracts/m9-ai-powers.md §3.2): empties the current user's Recycle Bin as it was at
/// approval. The plan's before records the item count and size; the approve recheck refuses a different count or size
/// (stale_plan), and so does a last read right before emptying. Verified when the bin reads empty afterwards.
/// Irreversible and loses data: no undo.
/// </summary>
internal sealed class EmptyRecycleBinHandler(IRecycleBin bin) : IActionHandler
{
    public const string Id = "cleanup.empty_recycle_bin";
    private const string ManualUri = "ms-settings:storagesense";
    internal const string Resource = "recycle_bin:user";

    public string ActionId => Id;
    public string Version => "1.0.0";
    public string Label => "Empty the Recycle Bin";
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "setting";
    public string Group => "storage";
    public string What => "Permanently deletes everything in your Recycle Bin, exactly as it was when you approved; nothing else is touched.";
    public IReadOnlyList<string> Flags => [ActionFlags.Irreversible, ActionFlags.DataLoss];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;

    internal static JsonObject Target() => new() { ["type"] = "recycle_bin", ["id"] = "user", ["label"] = "Recycle Bin" };

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
        bin.Query() is { } now ? State(now) : throw new IOException("The Recycle Bin can't be read."), ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(execution), ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Parameters is { Count: > 0 }) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");
        if (request.Target is { Count: > 0 } t && !JsonNode.DeepEquals(t, Target())) return ActionResolution.Ineligible("invalid_arguments", "This change takes no target.");
        if (request.Undo) return ActionResolution.Ineligible("not_supported", "Emptying the Recycle Bin can't be undone.");

        RecycleBinInfo? now;
        try { now = bin.Query(); }
        catch (Exception) { now = null; }
        if (now is null || now.Items < 0 || now.Bytes < 0) return ActionResolution.Ineligible("read_failed", "Windows didn't report the Recycle Bin.", ManualUri);
        if (now.Items == 0) return ActionResolution.Ineligible("already_set", "The Recycle Bin is already empty.");

        return new ActionResolution(
            Eligible: true, Target: Target(),
            Before: State(now), After: State(new RecycleBinInfo(0, 0)),
            Rows: [new PlanRow("Recycle Bin", $"{Items(now.Items)} · {Size(now.Bytes)}", "Empty")],
            Context: [new("Applies to", "Your account, every drive")],
            Tradeoffs: ["Deleted files can't be restored from the Recycle Bin afterwards.", "If anything is added before Optimus empties it, nothing is deleted and you preview again."],
            UndoConditions: null,
            Resources: [Resource]);
    }

    internal ActionOutcome Execute(ActionExecution ex)
    {
        // As it was at approval: a last read right before emptying; anything added or removed since → nothing is deleted.
        RecycleBinInfo? now;
        try { now = bin.Query(); }
        catch (Exception) { now = null; }
        if (now is null) return new ActionOutcome("failed", new JsonObject(), "Nothing was deleted: Windows didn't report the Recycle Bin.", "read_failed");
        if (!JsonNode.DeepEquals(State(now), ex.Before))
            return new ActionOutcome("failed", State(now), "Nothing was deleted: the Recycle Bin changed since the preview. Preview again.", "stale_plan");

        int hr;
        try { hr = bin.Empty(); }
        catch (Exception e) { hr = e.HResult; }

        RecycleBinInfo? after;
        try { after = bin.Query(); }
        catch (Exception) { after = null; }
        if (after is null) return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't report the Recycle Bin after emptying it.", "unexpected_state", Hr(hr));
        var observed = State(after);
        if (after.Items == 0) return new ActionOutcome("verified", State(new RecycleBinInfo(0, 0)), "The Recycle Bin is empty, read back from Windows. Nothing else was changed.");
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, "The Recycle Bin wasn't emptied.", hr < 0 ? "write_failed" : "not_applied", Hr(hr));
        return new ActionOutcome("indeterminate", observed, "Some items are still in the Recycle Bin.", "unexpected_state", Hr(hr));
    }

    internal static JsonObject State(RecycleBinInfo i) => new() { ["items"] = i.Items, ["bytes"] = i.Bytes };

    private static string? Hr(int hr) => hr < 0 ? $"HRESULT 0x{hr:X8}" : null;

    private static string Items(long n) => n == 1 ? "1 item" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} items";

    internal static string Size(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        var c = CultureInfo.CurrentCulture;
        return bytes >= Gb ? (bytes / Gb).ToString("0.#", c) + " GB" : bytes >= Mb ? (bytes / Mb).ToString("0.#", c) + " MB"
            : bytes >= Kb ? (bytes / Kb).ToString("0", c) + " KB" : bytes + " bytes";
    }
}
