using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Protection;

namespace OptimizationTool.Windows.Actions.Apps;

/// <summary>
/// One installed package of the current user as Windows reports it now. SignatureKind: none | developer | enterprise |
/// store | system. PublisherId is the package identity's publisher (e.g. "CN=Microsoft Windows, …").
/// </summary>
internal sealed record StorePackage(string FullName, string FamilyName, string? DisplayName, string? Publisher, string? PublisherId,
    bool IsFramework, bool IsResource, bool IsBundle, string SignatureKind);

/// <summary>Remove result: removed | error | timeout, with Windows' HRESULT when it gave one.</summary>
internal sealed record StoreRemoveResult(string Status, int HResult = 0);

/// <summary>
/// app.uninstall_store's seam (Windows.Management.Deployment.PackageManager). <see cref="Remove"/> is the only write.
/// Tests use a fake; no test may call the real Remove.
/// </summary>
internal interface IStorePackages
{
    /// <summary>The current user's installed packages of this family (usually one).</summary>
    IReadOnlyList<StorePackage> ForUser(string familyName);
    /// <summary>Removes the package; cancelling <paramref name="ct"/> cancels Windows' removal operation itself.</summary>
    Task<StoreRemoveResult> RemoveAsync(string fullName, CancellationToken ct);
}

/// <summary>Real implementation through the WinRT PackageManager (the same API the installed-apps collector reads). Written, never run by the tests.</summary>
internal sealed class WindowsStorePackages : IStorePackages
{
    public IReadOnlyList<StorePackage> ForUser(string familyName)
    {
        var pm = new global::Windows.Management.Deployment.PackageManager();
        var list = new List<StorePackage>();
        foreach (var p in pm.FindPackagesForUser(string.Empty, familyName))
        {
            string? display = null, publisher = null;
            try { display = p.DisplayName; } catch (Exception) { }
            try { publisher = p.PublisherDisplayName; } catch (Exception) { }
            var kind = p.SignatureKind switch
            {
                global::Windows.ApplicationModel.PackageSignatureKind.Developer => "developer",
                global::Windows.ApplicationModel.PackageSignatureKind.Enterprise => "enterprise",
                global::Windows.ApplicationModel.PackageSignatureKind.Store => "store",
                global::Windows.ApplicationModel.PackageSignatureKind.System => "system",
                _ => "none",
            };
            list.Add(new StorePackage(p.Id.FullName, p.Id.FamilyName, display, publisher, p.Id.Publisher, p.IsFramework, p.IsResourcePackage, p.IsBundle, kind));
        }
        return list;
    }

    public async Task<StoreRemoveResult> RemoveAsync(string fullName, CancellationToken ct)
    {
        var pm = new global::Windows.Management.Deployment.PackageManager();
        // AsTask(ct): a cancel (the handler's timeout, the engine's deadline, shutdown) calls Cancel() on the WinRT
        // operation, and the task ends only when Windows' operation has ended.
        var result = await pm.RemovePackageAsync(fullName).AsTask(ct).ConfigureAwait(false);
        var error = result.ExtendedErrorCode;
        return error is null || error.HResult == 0 ? new StoreRemoveResult("removed") : new StoreRemoveResult("error", error.HResult);
    }
}

/// <summary>
/// app.uninstall_store {targetRef} (contracts/m9-ai-powers.md §3.2): removes one Store (MSIX/AppX) app for this user only,
/// named by installed-apps package evidence (its local family name; the full name is read live and pinned in the plan).
/// Refused: frameworks, resource packages, system-signed and Windows packages, protected and hardware-list apps, and
/// anything not installed for this user. Irreversible (it can be installed again from the Store); no undo. Verified when
/// no package of that family is left for this user. Messages and receipts never name the app (the AI reads them); the
/// rows do.
/// </summary>
internal sealed partial class StoreUninstallHandler(IStorePackages packages, ISecurityProducts? products = null, TimeSpan? removeTimeout = null) : IActionHandler
{
    public const string Id = "app.uninstall_store";
    public const string CollectorId = "win.apps.installed";
    private const string ManualUri = "ms-settings:appsfeatures";
    /// <summary>The removal's own limit, inside the engine's 90 s setting deadline; on it the removal is cancelled.</summary>
    internal static readonly TimeSpan DefaultRemoveTimeout = TimeSpan.FromSeconds(75);
    private readonly TimeSpan _removeTimeout = removeTimeout ?? DefaultRemoveTimeout;

    [GeneratedRegex(@"^[A-Za-z0-9.\-]{1,64}_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyPattern();

    public string ActionId => Id;
    public string Version => "1.0.0";
    public string Label => "Remove a Store app";
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "setting";
    public string Group => "apps";
    public string What => "Removes one Microsoft Store app for your account only; you can install it again from the Store.";
    public IReadOnlyList<string> Flags => [ActionFlags.Irreversible];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;
    public string? TargetKind => "Store app (from installed apps)";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var (family, _) = FromTarget(target) ?? throw new ArgumentException("Not a Store app target.", nameof(target));
        return State(packages.ForUser(family));
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => ExecuteCoreAsync(execution, ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Parameters is { Count: > 0 }) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");
        if (request.Undo) return ActionResolution.Ineligible("not_supported", "Removing an app can't be undone; install it again from the Store.", ManualUri);

        // The package family: this handler's own plan target (recheck), or the local family name of package evidence.
        string family;
        string? pinned = null;
        if (request.Recheck)
        {
            if (request.Target is null || FromTarget(request.Target) is not { } t) return Unknown();
            (family, pinned) = t;
        }
        else
        {
            var ev = request.Evidence;
            if (ev is null || ev.CollectorId != CollectorId || !ev.Subject.StartsWith("apps.package.", StringComparison.Ordinal)) return Unknown();
            if (Str(ev.Value["familyName"]) is not { } f || !FamilyPattern().IsMatch(f)) return Unknown();
            family = f;
        }

        IReadOnlyList<StorePackage> found;
        try { found = packages.ForUser(family); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't list your apps.", ManualUri); }
        found = found.Where(p => string.Equals(p.FamilyName, family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) return ActionResolution.Ineligible("not_found", "That app isn't installed for your account. Check again.", ManualUri);
        if (found.Count > 1) return Unknown();
        var pkg = found[0];
        if (pinned is not null && !string.Equals(pkg.FullName, pinned, StringComparison.Ordinal))
            return ActionResolution.Ineligible("changed_since", "The app was updated since the preview. Preview again.", ManualUri);

        if (pkg.IsFramework || pkg.IsResource)
            return ActionResolution.Ineligible(Protection.Protection.ProtectedCode, "This is a component other apps use, not an app.", ManualUri);
        if (pkg.SignatureKind == "system" || pkg.PublisherId is { } pid && pid.StartsWith("CN=Microsoft Windows", StringComparison.OrdinalIgnoreCase))
            return ActionResolution.Ineligible(Protection.Protection.ProtectedCode, "Windows marks this app as part of the system.", ManualUri);
        if (ProtectedTargets.StoreApp(pkg.FamilyName, pkg.DisplayName, pkg.Publisher, (products ?? NoSecurityProducts.Instance).Names()) is { } prot)
            return ActionResolution.Ineligible(prot.Code, prot.Reason, ManualUri);

        var label = Label_(pkg);
        return new ActionResolution(
            Eligible: true,
            Target: new JsonObject { ["type"] = "store_app", ["id"] = pkg.FamilyName, ["fullName"] = pkg.FullName, ["label"] = label, ["publisher"] = Clean(pkg.Publisher) },
            Before: State([pkg]), After: State([]),
            Rows: [new PlanRow("Store app", label, "Removed")],
            Context: [new("Applies to", "Your account only"), new("Get it back", "Install it again from the Microsoft Store")],
            Tradeoffs: ["Its data and settings for your account are removed with it.", "Other accounts on this PC keep it."],
            UndoConditions: null,
            Resources: [$"appx:{pkg.FamilyName.ToLowerInvariant()}"]);
    }

    internal ActionOutcome Execute(ActionExecution ex) => ExecuteCoreAsync(ex, CancellationToken.None).GetAwaiter().GetResult();

    internal async Task<ActionOutcome> ExecuteCoreAsync(ActionExecution ex, CancellationToken ct)
    {
        if (FromTarget(ex.Target) is not { } t) return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");
        StoreRemoveResult result;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(_removeTimeout);
            try { result = await packages.RemoveAsync(t.FullName, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = new StoreRemoveResult("timeout"); }
            catch (Exception e) { result = new StoreRemoveResult("error", e.HResult); }
        }

        JsonObject observed;
        try { observed = State(packages.ForUser(t.Family)); }
        catch (Exception) { return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't list your apps after the change.", "unexpected_state", result.Status); }
        var hr = result.HResult == 0 ? null : $"HRESULT 0x{result.HResult:X8}";
        if (JsonNode.DeepEquals(observed, ex.After))
            return new ActionOutcome("verified", observed, "The app was removed for your account, read back from Windows. You can install it again from the Microsoft Store.");
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, "The app is still installed.", result.Status == "timeout" ? "timeout" : result.Status == "error" ? "write_failed" : "not_applied", hr);
        return new ActionOutcome("indeterminate", observed, "The app changed in a way that wasn't expected.", "unexpected_state", hr);
    }

    /// <summary>{installed, fullName} for the one package this user has; {installed:false} when none is left.</summary>
    private static JsonObject State(IReadOnlyList<StorePackage> found) => found.Count switch
    {
        0 => new JsonObject { ["installed"] = false },
        1 => new JsonObject { ["installed"] = true, ["fullName"] = found[0].FullName },
        _ => new JsonObject { ["installed"] = true, ["fullName"] = null, ["count"] = found.Count },
    };

    private static (string Family, string FullName)? FromTarget(JsonObject t) =>
        Str(t["type"]) == "store_app" && Str(t["id"]) is { } family && FamilyPattern().IsMatch(family)
        && Str(t["fullName"]) is { Length: > 0 and <= 512 } full && full.StartsWith(family.Split('_')[0] + "_", StringComparison.OrdinalIgnoreCase) && !full.Any(char.IsControl)
            ? (family, full) : null;

    private static string Label_(StorePackage p)
    {
        var name = Clean(p.DisplayName);
        return name is null || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? p.FamilyName.Split('_')[0] : name;
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length == 0 ? null : t.Length > 100 ? t[..100] : t;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static ActionResolution Unknown() =>
        ActionResolution.Ineligible(ActionService.UnknownTarget, "Optimus can't match that target to one Store app installed for you.");
}
