using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Collectors;

internal sealed record WscProductRaw(string Name, int State, int SignatureStatus, bool IsDefault, string? Guid);

internal sealed record WscRaw(
    int AvHr, int AvHealth, int FwHr, int FwHealth,
    IReadOnlyList<WscProductRaw>? AvProducts, string? AvError,
    IReadOnlyList<WscProductRaw>? FwProducts, string? FwError);

/// <summary>
/// win.security.wsc — Windows Security Center aggregate health (S_OK required for a usable value) and the
/// registered antivirus and firewall products via <c>IWSCProductList</c> (the default product is "primary").
/// </summary>
internal sealed class WscCollector(CollectorOptions options) : SecurityCollector<WscRaw>(options)
{
    public const string CollectorId = "win.security.wsc";
    private const int ProductCap = 32;

    public override string Id => CollectorId;
    public override string GapTitle => "Security Center";
    public override string GapSource => "Windows Security Center";
    public override string GapDisplay => "Windows Security Center did not answer.";

    internal override WscRaw Read(CancellationToken ct)
    {
        var avHr = Native.WscGetSecurityProviderHealth(Native.WSC_SECURITY_PROVIDER_ANTIVIRUS, out var avHealth);
        var fwHr = Native.WscGetSecurityProviderHealth(Native.WSC_SECURITY_PROVIDER_FIREWALL, out var fwHealth);
        ct.ThrowIfCancellationRequested();
        var (av, avErr) = Products(Native.WSC_SECURITY_PROVIDER_ANTIVIRUS);
        ct.ThrowIfCancellationRequested();
        var (fw, fwErr) = Products(Native.WSC_SECURITY_PROVIDER_FIREWALL);
        return new WscRaw(avHr, avHealth, fwHr, fwHealth, av, avErr, fw, fwErr);
    }

    private static (IReadOnlyList<WscProductRaw>?, string?) Products(int provider)
    {
        IWSCProductList? list = null;
        try
        {
            list = (IWSCProductList)Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_WSCProductList, throwOnError: true)!)!;
            list.Initialize((uint)provider);
            var count = Math.Min(list.GetCount(), ProductCap);
            var result = new List<WscProductRaw>();
            for (uint i = 0; i < count; i++)
            {
                var p = list.GetItem(i);
                try
                {
                    string? guid = null;
                    try { guid = p.GetProductGuid(); } catch (COMException) { }
                    var isDefault = false;
                    try { isDefault = p.GetProductIsDefault() != 0; } catch (COMException) { }
                    result.Add(new WscProductRaw(p.GetProductName() ?? "", p.GetProductState(), p.GetSignatureStatus(), isDefault, guid));
                }
                finally { Automation.Release(p); }
            }
            return (result, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return (null, Automation.Hex(ex.HResult));
        }
        finally { Automation.Release(list); }
    }

    internal override CollectorResult Normalize(WscRaw raw)
    {
        var facts = new FactSet("Windows Security Center");
        var problems = new List<string>();

        AddHealth(facts, "antivirus", raw.AvHr, raw.AvHealth, problems);
        AddHealth(facts, "firewall", raw.FwHr, raw.FwHealth, problems);
        AddProducts(facts, "av", "antivirus", raw.AvProducts, raw.AvError, problems);
        AddProducts(facts, "fw", "firewall", raw.FwProducts, raw.FwError, problems);

        var status = problems.Count == 0 ? "complete" : facts.Count == 0 ? "failed" : "partial";
        return facts.Result(status, problems.Count == 0 ? null : string.Join(" ", problems));
    }

    private static void AddHealth(FactSet facts, string kind, int hr, int health, List<string> problems)
    {
        var usable = hr == 0;
        var label = usable ? Health(health) : "unavailable";
        if (!usable) problems.Add($"Security Center gave no usable {kind} status ({Automation.Hex(hr)}).");
        facts.Add($"security.wsc.health.{kind}", new JsonObject
        {
            ["provider"] = kind,
            ["hresult"] = Automation.Hex(hr),
            ["usable"] = usable,
            ["health"] = label,
        }, usable
            ? $"Windows Security Center reports {kind} protection as {label.Replace('_', ' ')}."
            : $"Windows Security Center did not give a usable {kind} status.");
    }

    private static void AddProducts(FactSet facts, string prefix, string kind, IReadOnlyList<WscProductRaw>? products, string? error, List<string> problems)
    {
        if (products is null)
        {
            problems.Add($"Registered {kind} products could not be listed ({error ?? "unknown"}).");
            return;
        }
        facts.Add($"security.wsc.products.{prefix}", new JsonObject
        {
            ["provider"] = kind,
            ["count"] = products.Count,
            ["onCount"] = products.Count(p => p.State == 0),
        }, products.Count == 1 ? $"1 {kind} product is registered with Windows." : $"{products.Count} {kind} products are registered with Windows.");

        foreach (var p in products)
        {
            var name = Text.Clean(p.Name);
            if (name.Length == 0) name = "Unnamed product";
            var state = p.State switch { 0 => "on", 1 => "off", 2 => "snoozed", 3 => "expired", _ => "unknown" };
            var role = p.IsDefault ? "primary" : p.State == 0 ? "active" : "inactive";
            var identity = string.IsNullOrWhiteSpace(p.Guid) ? name : p.Guid!;
            facts.Add($"security.{prefix}.product.{Subjects.Hash8(identity)}", new JsonObject
            {
                ["provider"] = kind,
                ["name"] = name,
                ["state"] = state,
                ["signatures"] = kind == "antivirus" ? (p.SignatureStatus == 1 ? "up_to_date" : "out_of_date") : null,
                ["role"] = role,
                ["isDefault"] = p.IsDefault,
                ["isMicrosoft"] = IsMicrosoft(name),
            }, $"{name} is registered for {kind} and reports {state}.");
        }
    }

    internal static bool IsMicrosoft(string name) =>
        name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Windows Firewall", StringComparison.OrdinalIgnoreCase);

    private static string Health(int h) => h switch { 0 => "good", 1 => "not_monitored", 2 => "poor", 3 => "snoozed", _ => "unknown" };
}
