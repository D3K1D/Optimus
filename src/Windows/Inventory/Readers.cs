using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static OptimizationTool.Windows.Inventory.Native;

namespace OptimizationTool.Windows.Inventory;

/// <summary>Windows calls only. Every method is read-only, standard-user, and returns raw DTOs (null = unreadable).</summary>
internal static unsafe class Readers
{
    static readonly Guid ClassMouse = new("4d36e96f-e325-11ce-bfc1-08002be10318");
    static readonly Guid ClassKeyboard = new("4d36e96b-e325-11ce-bfc1-08002be10318");
    static readonly Guid ClassDisplay = new("4d36e968-e325-11ce-bfc1-08002be10318");
    static readonly Guid ClassHid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    static readonly Guid InterfaceUsbHub = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    static readonly Guid DriverKeys = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    static readonly Guid DeviceKeys = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, DIGCF_DEVICEINTERFACE = 0x10;

    // ---- Specs ----
    public static SpecsRaw ReadSpecs()
    {
        int? logical = null, physical = null;
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, null, ref len);
            if (len > 0)
            {
                var buf = new byte[len];
                fixed (byte* p = buf)
                {
                    if (GetLogicalProcessorInformationEx(0, p, ref len))
                    {
                        int cores = 0, threads = 0;
                        for (uint off = 0; off + 8 <= len;)
                        {
                            uint size = *(uint*)(p + off + 4);
                            if (size == 0) break;
                            cores++;
                            ushort groups = *(ushort*)(p + off + 8 + 22);
                            for (int g = 0; g < groups; g++)
                                threads += BitOperations.PopCount(*(ulong*)(p + off + 32 + g * 16));
                            off += size;
                        }
                        if (cores > 0) { physical = cores; logical = threads > 0 ? threads : null; }
                    }
                }
            }
        }
        catch (Exception) { }
        logical ??= Environment.ProcessorCount > 0 ? Environment.ProcessorCount : null;

        ulong? kb = null;
        if (GetPhysicallyInstalledSystemMemory(out var installed) && installed > 0) kb = installed;

        string? edition = null; int? build = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            edition = key?.GetValue("ProductName") as string;
            if (int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var b)) build = b;
        }
        catch (Exception) { }

        bool? battery = null;
        if (GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 255) battery = (ps.BatteryFlag & 128) == 0;

        return new SpecsRaw(logical, physical, kb, edition, build, battery);
    }

    // ---- Graphics ----
    public static GraphicsRaw ReadGraphics()
    {
        int? gpus = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var ptr) >= 0 && ptr != IntPtr.Zero)
            {
                var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(ptr);
                Marshal.Release(ptr);
                int count = 0;
                try
                {
                    for (uint i = 0; i < 16 && factory.EnumAdapters1(i, out var adapter) >= 0; i++)
                    {
                        try
                        {
                            if (adapter.GetDesc1(out var d) >= 0 && !InventoryNormalize.IsSoftwareAdapter(d.Flags, d.VendorId, d.DeviceId)) count++;
                        }
                        finally { Marshal.ReleaseComObject(adapter); }
                    }
                    gpus = count;
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
        }
        catch (Exception) { }

        IReadOnlyList<DriverRec>? drivers = null;
        try { drivers = EnumPresent(ClassDisplay, withIdentity: false).Select(d => d.Driver).ToList(); }
        catch (Exception) { }
        return new GraphicsRaw(gpus, drivers);
    }

    // ---- Displays ----
    public static DisplaysRaw ReadDisplays()
    {
        // DISPLAY_DEVICEW: cb(4) name(64) string(256) flags(4) id(256) key(256) = 840 bytes; StateFlags at 324.
        const int ddSize = 840, ddFlagsOffset = 4 + 64 + 256;
        const int dmSize = 220, dmFreqOffset = 184;
        var dd = new byte[ddSize];
        var dm = new byte[dmSize];
        int count = 0, maxHz = 0;
        bool any = false;
        fixed (byte* pdd = dd)
        fixed (byte* pdm = dm)
        {
            for (uint i = 0; i < 64; i++)
            {
                Array.Clear(dd); *(uint*)pdd = ddSize;
                if (!EnumDisplayDevicesW(null, i, pdd, 0)) break;
                any = true;
                uint flags = *(uint*)(pdd + ddFlagsOffset);
                if ((flags & 1) == 0 || (flags & 8) != 0) continue; // attached to desktop, not a mirroring driver
                count++;
                string name = new string((char*)(pdd + 4));
                Array.Clear(dm); *(ushort*)(pdm + 68) = dmSize;
                if (EnumDisplaySettingsW(name, -1, pdm))
                {
                    int hz = (int)*(uint*)(pdm + dmFreqOffset);
                    if (hz > 1 && hz > maxHz) maxHz = hz;
                }
            }
        }
        return new DisplaysRaw(any ? count : null, maxHz > 0 ? maxHz : null);
    }

    // ---- Devices ----
    public static DevicesRaw ReadDevices()
    {
        List<PointerKind>? pointers = null;
        int? keyboards = null;
        try
        {
            var mice = EnumPresent(ClassMouse, withIdentity: true).Where(d => !IsVirtualInstance(d.InstanceId)).ToList();
            pointers = mice.Select(d => InventoryNormalize.ClassifyPointer(d.InstanceId, d.Name, d.Ancestors)).ToList();
            // Precision touchpads often only appear as an HID "touch pad" entry.
            bool hidTouchpad = EnumPresent(ClassHid, withIdentity: true).Any(d => InventoryNormalize.NameSaysTouchpad(d.Name));
            if (hidTouchpad && !pointers.Contains(PointerKind.Touchpad)) pointers.Add(PointerKind.Touchpad);
        }
        catch (Exception) { pointers = null; }
        try { keyboards = EnumPresent(ClassKeyboard, withIdentity: true).Count(d => !IsVirtualInstance(d.InstanceId)); }
        catch (Exception) { }

        List<int?>? hubs = null; int root = 0;
        try
        {
            hubs = [];
            foreach (var (instanceId, path) in EnumHubInterfaces())
            {
                if (instanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) { root++; continue; }
                hubs.Add(ReadHubPorts(path));
            }
        }
        catch (Exception) { hubs = null; }
        return new DevicesRaw(pointers, keyboards, hubs, root);
    }

    static bool IsVirtualInstance(string id) =>
        id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);

    static List<(string InstanceId, string Path)> EnumHubInterfaces()
    {
        var g = InterfaceUsbHub;
        var set = SetupDiGetClassDevsW(&g, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == InvalidHandle) return [];
        var found = new List<(string, string)>();
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, &g, i, ref ifd)) break;
                var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
                var detail = new byte[1024];
                fixed (byte* pd = detail)
                {
                    *(uint*)pd = 8; // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) on x64
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifd, pd, (uint)detail.Length, out _, ref info)) continue;
                    string path = new((char*)(pd + 4));
                    found.Add((InstanceIdOf(set, ref info), path));
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return found;
    }

    static int? ReadHubPorts(string path)
    {
        // Query-only: open with no access rights, fall back to the access hubs usually require for the same query.
        foreach (uint access in new uint[] { 0, 0x40000000 })
        {
            var h = CreateFileW(path, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == InvalidHandle || h == IntPtr.Zero) continue;
            try
            {
                var buf = new byte[256]; // USB_NODE_INFORMATION; NodeType 0 = UsbHub
                fixed (byte* p = buf)
                {
                    if (DeviceIoControl(h, 0x220408, p, (uint)buf.Length, p, (uint)buf.Length, out _, IntPtr.Zero))
                    {
                        int ports = buf[6]; // HubDescriptor.bNumberOfPorts
                        return ports is > 0 and <= 255 ? ports : null;
                    }
                }
            }
            finally { CloseHandle(h); }
        }
        return null;
    }

    // ---- Audio ----
    public static AudioRaw ReadAudio() => new(CountEndpoints(0), CountEndpoints(1));

    static int? CountEndpoints(int dataFlow)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            try
            {
                if (enumerator.EnumAudioEndpoints(dataFlow, 1 /* DEVICE_STATE_ACTIVE */, out var coll) < 0) return null;
                try { return coll.GetCount(out var n) >= 0 ? (int)n : null; }
                finally { Marshal.ReleaseComObject(coll); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch (Exception) { return null; }
    }

    // ---- Network ----
    public static NetworkRaw ReadNetwork()
    {
        const uint skip = 0x1 | 0x2 | 0x4 | 0x8; // no unicast/anycast/multicast/DNS: addresses are never read
        uint size = 15000;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buf = new byte[size];
            fixed (byte* p = buf)
            {
                uint rc = GetAdaptersAddresses(0, skip, IntPtr.Zero, p, ref size);
                if (rc == 111) continue; // ERROR_BUFFER_OVERFLOW: size updated
                if (rc != 0) return new NetworkRaw(null);
                var list = new List<AdapterRec>();
                for (byte* a = p; a != null; a = *(byte**)(a + 8))
                {
                    uint ifIndex = *(uint*)(a + 4);
                    uint ifType = *(uint*)(a + 100);
                    uint oper = *(uint*)(a + 104);
                    bool wifi = ifType == 71, eth = ifType == 6;
                    if (!wifi && !eth) continue;
                    list.Add(new AdapterRec(wifi, eth, oper == 1, IsHardwareInterface(ifIndex, ifType)));
                }
                return new NetworkRaw(list);
            }
        }
        return new NetworkRaw(null);
    }

    // MIB_IF_ROW2 (1352 bytes): InterfaceIndex@8, Type@1128, InterfaceAndOperStatusFlags@1152 (bit0 = HardwareInterface).
    static bool? IsHardwareInterface(uint ifIndex, uint expectedType)
    {
        try
        {
            var row = new byte[1352];
            fixed (byte* p = row)
            {
                *(uint*)(p + 8) = ifIndex;
                if (GetIfEntry2(p) != 0) return null;
                if (*(uint*)(p + 1128) != expectedType) return null; // layout sanity check
                return (p[1152] & 1) != 0;
            }
        }
        catch (Exception) { return null; }
    }

    // ---- Drivers ----
    public static DriversRaw ReadDrivers()
    {
        try { return new DriversRaw(EnumPresent(null, withIdentity: false).Select(d => d.Driver).ToList()); }
        catch (Exception) { return new DriversRaw(null); }
    }

    // ---- SetupAPI enumeration of present devices ----
    sealed record Dev(string InstanceId, string Name, IReadOnlyList<string> Ancestors, DriverRec Driver);

    static List<Dev> EnumPresent(Guid? cls, bool withIdentity)
    {
        var result = new List<Dev>();
        var g = cls ?? default;
        var set = SetupDiGetClassDevsW(cls is null ? null : &g, null, IntPtr.Zero, cls is null ? DIGCF_PRESENT | DIGCF_ALLCLASSES : DIGCF_PRESENT);
        if (set == InvalidHandle) throw new InvalidOperationException("SetupDiGetClassDevs failed");
        try
        {
            for (uint i = 0; i < 4096; i++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
                if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;

                DateTime? date = null;
                var dateBytes = Prop(set, ref info, DriverKeys, 2, 0x10);
                if (dateBytes is { Length: >= 8 })
                {
                    long ft = BitConverter.ToInt64(dateBytes, 0);
                    if (ft > 0) { try { date = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { } }
                }
                string? version = Str(Prop(set, ref info, DriverKeys, 3, 0x12));
                string? provider = Str(Prop(set, ref info, DriverKeys, 9, 0x12));
                var driver = new DriverRec(date, version, provider);

                if (!withIdentity) { result.Add(new Dev("", "", [], driver)); continue; }

                string id = InstanceIdOf(set, ref info);
                string name = (Str(Prop(set, ref info, DeviceKeys, 14, 0x12)) ?? Str(Prop(set, ref info, DeviceKeys, 2, 0x12)) ?? "");
                result.Add(new Dev(id, name, Ancestors(info.DevInst), driver));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    static List<string> Ancestors(uint devInst)
    {
        var list = new List<string>();
        for (int depth = 0; depth < 4; depth++)
        {
            if (CM_Get_Parent(out var parent, devInst, 0) != 0) break;
            var arr = new char[512];
            fixed (char* buf = arr)
            {
                if (CM_Get_Device_IDW(parent, buf, 512, 0) != 0) break;
                list.Add(new string(buf));
            }
            devInst = parent;
        }
        return list;
    }

    static string InstanceIdOf(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var arr = new char[512]; fixed (char* buf = arr)
        return SetupDiGetDeviceInstanceIdW(set, ref info, buf, 512, out _) ? new string(buf) : "";
    }

    static byte[]? Prop(IntPtr set, ref SP_DEVINFO_DATA info, Guid fmt, uint pid, uint expectedType)
    {
        var key = new DEVPROPKEY { fmtid = fmt, pid = pid };
        var buf = new byte[1024];
        fixed (byte* p = buf)
        {
            if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, p, (uint)buf.Length, out var req, 0)) return null;
            if (type != expectedType) return null;
            return buf.AsSpan(0, (int)Math.Min(req, (uint)buf.Length)).ToArray();
        }
    }

    static string? Str(byte[]? b)
    {
        if (b is null || b.Length < 2) return null;
        var s = System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }
}
