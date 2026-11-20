using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Windows.Actions.Settings;

/// <summary>One registry value: hive "HKCU" | "HKLM", an explicit view (64 | 32), the key path and the value name (null = the key itself).</summary>
internal sealed record RegLocation(string Hive, int View, string Key, string? Name);

/// <summary>
/// A value as read: absent, or its type ("dword" | "string" | "expand_string" | "binary" | "other") and data (a number
/// for dword, the unexpanded text for string and expand_string, base64 for binary, null for anything else). The JSON
/// shape is what plans, receipts and drift checks compare.
/// </summary>
internal sealed record RegData(bool Present, string? Type, JsonNode? Data)
{
    public static readonly RegData Absent = new(false, null, null);
    public static RegData Dword(uint value) => new(true, "dword", JsonValue.Create((long)value));
    public static RegData String(string value) => new(true, "string", JsonValue.Create(value));
    public static RegData Binary(byte[] value) => new(true, "binary", JsonValue.Create(Convert.ToBase64String(value)));
}

/// <summary>
/// The settings handler's registry seam. Every call names its view explicitly and never follows a registry link.
/// Write and Delete read the value back on the same open key and return what they read. Tests use a fake; no test
/// may call the real implementation's writes.
/// </summary>
internal interface IRegistryStore
{
    /// <summary>The value, or <see cref="RegData.Absent"/> when the key or the value does not exist.</summary>
    RegData Read(RegLocation at);
    /// <summary>Creates missing keys, writes the value ("dword" or "string") and returns the read-back.</summary>
    RegData Write(RegLocation at, string type, JsonNode data);
    /// <summary>Deletes the value (absent is fine) and returns the read-back.</summary>
    RegData Delete(RegLocation at);
    /// <summary>A policy key exists (Name null) or the policy value is present.</summary>
    bool Exists(RegLocation at);
    /// <summary>The value names of the key at <paramref name="key"/> (its Name is ignored); empty when the key is missing.</summary>
    IReadOnlyList<string> ValueNames(RegLocation key);
}

/// <summary>Real registry access through advapi32, walking the path one key at a time with REG_OPTION_OPEN_LINK.</summary>
internal sealed partial class WindowsRegistryStore : IRegistryStore
{
    private const int KeyQueryValue = 0x1, KeySetValue = 0x2, KeyCreateSubKey = 0x4, KeyWow64_64 = 0x100, KeyWow64_32 = 0x200;
    private const int OptionOpenLink = 0x8, ErrorSuccess = 0, ErrorFileNotFound = 2, RegLink = 6, OpenedExisting = 2;

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(nint hKey, string subKey, int options, int sam, out nint result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegCreateKeyEx(nint hKey, string subKey, int reserved, string? cls, int options, int sam, nint security, out nint result, out int disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueEx(nint hKey, string? name, nint reserved, out int type, nint data, ref int size);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint hKey);

    private static nint Root(string hive) => hive switch
    {
        "HKCU" => unchecked((int)0x80000001),
        "HKLM" => unchecked((int)0x80000002),
        _ => throw new ArgumentException("Unsupported hive.", nameof(hive)),
    };

    private static int ViewFlag(int view) => view == 32 ? KeyWow64_32 : view == 64 ? KeyWow64_64 : throw new ArgumentException("A registry view must be 32 or 64.", nameof(view));

    private static bool IsLink(nint key)
    {
        var size = 0;
        return RegQueryValueEx(key, "SymbolicLinkValue", 0, out var type, 0, ref size) == ErrorSuccess && type == RegLink;
    }

    /// <summary>Opens (or, when <paramref name="create"/>, creates) the key without following links; null when it is missing.</summary>
    private static RegistryKey? Open(RegLocation at, bool create, bool writable)
    {
        var view = ViewFlag(at.View);
        var parent = Root(at.Hive);
        var owned = false;
        try
        {
            var parts = at.Key.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                var sam = view | KeyQueryValue | (create ? KeyCreateSubKey : 0) | (writable && last ? KeySetValue : 0);
                var rc = RegOpenKeyEx(parent, parts[i], OptionOpenLink, sam, out var child);
                if (rc == ErrorFileNotFound)
                {
                    if (!create) return null;
                    rc = RegCreateKeyEx(parent, parts[i], 0, null, 0, sam, 0, out child, out var disposition);
                    if (rc == ErrorSuccess && disposition == OpenedExisting)
                    {
                        // Appeared between the two calls: reopen it as a link-aware handle before trusting it.
                        RegCloseKey(child);
                        rc = RegOpenKeyEx(parent, parts[i], OptionOpenLink, sam, out child);
                    }
                }
                if (rc != ErrorSuccess) throw new IOException($"Registry error {rc}.");
                if (IsLink(child))
                {
                    RegCloseKey(child);
                    throw new IOException("The registry path contains a link.");
                }
                if (owned) RegCloseKey(parent);
                parent = child;
                owned = true;
            }
            if (!owned) throw new ArgumentException("A key path is required.", nameof(at));
            owned = false; // handed to the RegistryKey below
            return RegistryKey.FromHandle(new SafeRegistryHandle(parent, ownsHandle: true), at.View == 32 ? RegistryView.Registry32 : RegistryView.Registry64);
        }
        finally
        {
            if (owned) RegCloseKey(parent);
        }
    }

    private static RegData ReadFrom(RegistryKey key, string? name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return RegData.Absent;
        return key.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => RegData.Dword(unchecked((uint)(int)value)),
            RegistryValueKind.String => RegData.String((string)value),
            RegistryValueKind.ExpandString => new RegData(true, "expand_string", JsonValue.Create((string)value)),
            RegistryValueKind.Binary => RegData.Binary((byte[])value),
            _ => new RegData(true, "other", null),
        };
    }

    public RegData Read(RegLocation at)
    {
        using var key = Open(at, create: false, writable: false);
        return key is null ? RegData.Absent : ReadFrom(key, at.Name);
    }

    public RegData Write(RegLocation at, string type, JsonNode data)
    {
        using var key = Open(at, create: true, writable: true) ?? throw new IOException("The key could not be created.");
        switch (type)
        {
            case "dword": key.SetValue(at.Name, unchecked((int)data.GetValue<long>()), RegistryValueKind.DWord); break;
            case "string": key.SetValue(at.Name, data.GetValue<string>(), RegistryValueKind.String); break;
            case "binary": key.SetValue(at.Name, Convert.FromBase64String(data.GetValue<string>()), RegistryValueKind.Binary); break;
            default: throw new ArgumentException("Unsupported value type.", nameof(type));
        }
        return ReadFrom(key, at.Name);
    }

    public RegData Delete(RegLocation at)
    {
        using var key = Open(at, create: false, writable: true);
        if (key is null) return RegData.Absent;
        key.DeleteValue(at.Name ?? "", throwOnMissingValue: false);
        return ReadFrom(key, at.Name);
    }

    public IReadOnlyList<string> ValueNames(RegLocation key)
    {
        using var k = Open(key with { Name = null }, create: false, writable: false);
        return k is null ? [] : k.GetValueNames().Take(1000).ToList();
    }

    public bool Exists(RegLocation at)
    {
        using var key = Open(at, create: false, writable: false);
        return key is not null && (at.Name is null || ReadFrom(key, at.Name).Present);
    }
}
