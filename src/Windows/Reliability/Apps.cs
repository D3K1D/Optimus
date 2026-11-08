using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability;

/// <summary>An Uninstall-key registration (only the fields we keep; UninstallString is never read).</summary>
internal sealed record RawUninstallEntry(string Scope, string KeyName, string? DisplayName, string? Publisher, string? Version,
    long? EstimatedSizeKb, bool SystemComponent, bool HasParent, string? ReleaseType);

internal sealed record RawPackage(string FamilyName, string? DisplayName, string? Publisher, string? Version);

/// <summary>Sources: per-scope ok flag (hklm64, hklm32, hkcu, packages).</summary>
internal sealed record RawApps(IReadOnlyList<RawUninstallEntry> Entries, IReadOnlyList<RawPackage> Packages, IReadOnlyDictionary<string, string> SourceStatus);

internal sealed class InstalledAppsCollector(CollectorOptions options) : ReliabilityCollector(options)
{
    public const int EntryCap = 5000;

    public override string Id => "win.apps.installed";
    public override string Category => "storage";
    public override string GapTitle => "Installed apps";
    public override string GapSource => "App registrations";

    protected override CollectorResult Collect(CancellationToken ct) => AppsNormalizer.Normalize(AppsReader.Read(ct));
}

internal static class AppsReader
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static RawApps Read(CancellationToken ct)
    {
        var entries = new List<RawUninstallEntry>();
        var status = new Dictionary<string, string>();
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, "hklm64", entries, status, ct);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, "hklm32", entries, status, ct);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Default, "hkcu", entries, status, ct);
        var packages = new List<RawPackage>();
        try
        {
            packages = ReadPackages(ct);
            status["packages"] = "ok";
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { status["packages"] = "denied"; }
        catch (Exception e) { status["packages"] = e.GetType().Name; }
        return new RawApps(entries, packages, status);
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, string scope, List<RawUninstallEntry> entries, Dictionary<string, string> status, CancellationToken ct)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(UninstallPath, writable: false);
            if (key is null) { status[scope] = "ok"; return; }
            foreach (var name in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= InstalledAppsCollector.EntryCap) { status[scope] = "row cap"; return; }
                try
                {
                    using var sub = key.OpenSubKey(name, writable: false);
                    if (sub is null) continue;
                    entries.Add(new RawUninstallEntry(scope, name,
                        sub.GetValue("DisplayName") as string,
                        sub.GetValue("Publisher") as string,
                        sub.GetValue("DisplayVersion") as string,
                        sub.GetValue("EstimatedSize") is int kb && kb > 0 ? kb : null,
                        sub.GetValue("SystemComponent") is int sc && sc == 1,
                        sub.GetValue("ParentKeyName") is string pk && pk.Length > 0,
                        sub.GetValue("ReleaseType") as string));
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
            status[scope] = "ok";
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Security.SecurityException) { status[scope] = "denied"; }
        catch (UnauthorizedAccessException) { status[scope] = "denied"; }
        catch (Exception e) { status[scope] = e.GetType().Name; }
    }

    private static List<RawPackage> ReadPackages(CancellationToken ct)
    {
        var pm = new global::Windows.Management.Deployment.PackageManager();
        var list = new List<RawPackage>();
        foreach (var p in pm.FindPackagesForUser(string.Empty))
        {
            ct.ThrowIfCancellationRequested();
            if (list.Count >= InstalledAppsCollector.EntryCap) break;
            try
            {
                if (p.IsFramework || p.IsResourcePackage) continue;
                if (p.SignatureKind == global::Windows.ApplicationModel.PackageSignatureKind.System) continue;
                var v = p.Id.Version;
                string? display = null, publisher = null;
                try { display = p.DisplayName; } catch (Exception) { }
                try { publisher = p.PublisherDisplayName; } catch (Exception) { }
                list.Add(new RawPackage(p.Id.FamilyName, display, publisher, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
        }
        return list;
    }
}

internal static class AppsNormalizer
{
    private static readonly Regex KbName = new(@"\bKB\d{6,8}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RuntimeName = new(
        @"\b(redistributable|runtime|redist|\.net|sdk|targeting pack|desktop runtime|vc\+\+|visual c\+\+|webview2|directx|driver|shared framework)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CollectorResult Normalize(RawApps raw)
    {
        var facts = new List<Fact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int skippedSystem = 0, skippedUpdates = 0, withSize = 0, runtimes = 0;

        foreach (var e in raw.Entries.OrderBy(e => e.Scope, StringComparer.Ordinal).ThenBy(e => e.KeyName, StringComparer.OrdinalIgnoreCase))
        {
            var name = R.Clean(e.DisplayName, 100);
            if (name.Length == 0) continue;
            if (e.SystemComponent) { skippedSystem++; continue; }
            if (e.HasParent || KbName.IsMatch(name) || KbName.IsMatch(e.KeyName) ||
                (e.ReleaseType is { } rt && Regex.IsMatch(rt, "update|hotfix", RegexOptions.IgnoreCase))) { skippedUpdates++; continue; }
            var publisher = R.Clean(e.Publisher, 80);
            var version = R.Clean(e.Version, 40);
            var identity = $"{name}\0{publisher}\0{version}".ToLowerInvariant();
            if (!seen.Add(identity)) continue;
            var kind = RuntimeName.IsMatch(name) ? "runtime" : "app";
            if (kind == "runtime") runtimes++;
            long? bytes = e.EstimatedSizeKb is { } kb ? kb * 1024L : null;
            if (bytes is not null) withSize++;
            facts.Add(new Fact($"apps.installed.{R.Hash8(identity)}", "App registrations", bytes is null ? "unavailable" : "reported", new JsonObject
            {
                ["name"] = name,
                ["publisher"] = publisher.Length > 0 ? publisher : null,
                ["version"] = version.Length > 0 ? version : null,
                ["source"] = "uninstall",
                ["scope"] = e.Scope,
                ["kind"] = kind,
                ["sizeBytes"] = bytes,
                ["sizeQuality"] = bytes is null ? "unknown" : "reported",
            }, bytes is { } b ? $"{name} reports about {Size(b)}." : $"{name} is installed; size unknown.", null));
        }

        var packages = 0;
        foreach (var p in raw.Packages.OrderBy(p => p.FamilyName, StringComparer.Ordinal))
        {
            var identity = "msix\0" + p.FamilyName.ToLowerInvariant();
            if (!seen.Add(identity)) continue;
            var name = R.Clean(p.DisplayName, 100);
            if (name.Length == 0 || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = R.Clean(p.FamilyName.Split('_')[0], 100);
            var publisher = R.Clean(p.Publisher, 80);
            packages++;
            facts.Add(new Fact($"apps.package.{R.Hash8(identity)}", "App packages", "measured", new JsonObject
            {
                ["name"] = name,
                ["publisher"] = publisher.Length > 0 && !publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? publisher : null,
                ["version"] = p.Version,
                ["source"] = "package",
                ["scope"] = "user",
                ["kind"] = "app",
                ["sizeBytes"] = null,
                ["sizeQuality"] = "unknown",
            }, $"{name} is installed from the Store or a package; size unknown.", null));
        }

        var registered = facts.Count - packages;
        facts.Add(new Fact("apps.installed.summary", "App registrations", "measured", new JsonObject
        {
            ["registeredCount"] = registered,
            ["packageCount"] = packages,
            ["withReportedSizeCount"] = withSize,
            ["runtimeCount"] = runtimes,
            ["skippedSystemComponentCount"] = skippedSystem,
            ["skippedUpdateCount"] = skippedUpdates,
            ["otherUsersCovered"] = false,
            ["portableAppsCovered"] = false,
        }, $"{R.Plural(registered + packages, "app", "apps")} found; {withSize} report a size.", null));

        var failed = raw.SourceStatus.Where(kv => kv.Value != "ok").Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        string status;
        if (failed.Count == 0) status = "complete";
        else if (failed.Count == raw.SourceStatus.Count && raw.SourceStatus.Values.All(v => v == "denied")) status = "denied";
        else status = "partial";
        return new CollectorResult(R.Sorted(facts), new CoverageReceipt(status, facts.Count - 1, failed.Count > 0 ? string.Join("; ", failed) : null));
    }

    internal static string Size(long bytes)
    {
        const double Gb = 1024d * 1024 * 1024, Mb = 1024d * 1024;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return bytes >= Gb ? string.Create(c, $"{bytes / Gb:0.#} GB") : string.Create(c, $"{Math.Max(1, Math.Round(bytes / Mb))} MB");
    }
}
