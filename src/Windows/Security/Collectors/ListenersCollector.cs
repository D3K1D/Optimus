using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Collectors;

internal sealed record ListenerRowRaw(string Protocol, string Family, byte[] Address, int Port, uint Pid);

internal sealed record ProcessRaw(string? ImagePath, DateTime? StartUtc, bool Denied);

internal sealed record ListenersRaw(
    IReadOnlyList<ListenerRowRaw> Rows, IReadOnlyDictionary<uint, ProcessRaw> Processes,
    DateTime SnapshotUtc, IReadOnlyList<string> TableErrors);

/// <summary>
/// win.security.listeners — TCP listeners and UDP bound endpoints (IPv4 + IPv6) from IP Helper
/// <c>GetExtendedTcpTable</c>/<c>GetExtendedUdpTable</c> with owner PID; the owner's image and start time via
/// <c>QueryFullProcessImageName</c>/<c>GetProcessTimes</c>. A process that started after the table snapshot is a
/// reused PID and is not attributed. Access denied leaves the image unknown but keeps the row.
/// Addresses are reduced to a bind scope (loopback/specific/wildcard); no address is stored.
/// </summary>
internal sealed class ListenersCollector(CollectorOptions options, PathRoots? roots = null) : SecurityCollector<ListenersRaw>(options)
{
    public const string CollectorId = "win.security.listeners";
    internal const int RowCap = 512;
    private readonly PathRoots _roots = roots ?? PathRoots.FromEnvironment();

    public override string Id => CollectorId;
    public override string GapTitle => "Listening ports";
    public override string GapSource => "Windows network tables";
    public override string GapDisplay => "Listening ports could not be read.";

    internal override ListenersRaw Read(CancellationToken ct)
    {
        var rows = new List<ListenerRowRaw>();
        var errors = new List<string>();
        var snapshot = DateTime.UtcNow;
        ReadTable(true, Native.AF_INET, rows, errors);
        ReadTable(true, Native.AF_INET6, rows, errors);
        ReadTable(false, Native.AF_INET, rows, errors);
        ReadTable(false, Native.AF_INET6, rows, errors);
        ct.ThrowIfCancellationRequested();

        var startTimes = ProcessStartTimes();
        var devices = DeviceMap();
        var processes = new Dictionary<uint, ProcessRaw>();
        foreach (var pid in rows.Select(r => r.Pid).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var p = QueryProcess(pid);
            if (p.ImagePath is null && pid is not (0 or 4))
            {
                // Fallback without a process handle: the kernel's image name for the PID, start time from the
                // process list. Both are readable by a standard user.
                var image = ImageByPid(pid, devices);
                var start = p.StartUtc ?? (startTimes.TryGetValue(pid, out var st) ? st : null);
                p = new ProcessRaw(image, image is null ? null : start, image is null && p.Denied);
            }
            processes[pid] = p;
        }
        return new ListenersRaw(rows, processes, snapshot, errors);
    }

    /// <summary>PID → creation time from NtQuerySystemInformation(SystemProcessInformation).</summary>
    private static Dictionary<uint, DateTime?> ProcessStartTimes()
    {
        var map = new Dictionary<uint, DateTime?>();
        var size = 1 << 20;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buf, size, out var needed);
                if (status == Native.STATUS_INFO_LENGTH_MISMATCH) { size = Math.Max(size * 2, needed + 65536); continue; }
                if (status != 0) return map;
                var offset = 0;
                for (var n = 0; n < 8192; n++)
                {
                    var entry = buf + offset;
                    var create = Marshal.ReadInt64(entry, 32);
                    var pid = (uint)Marshal.ReadIntPtr(entry, 80).ToInt64();
                    map[pid] = create > 0 ? DateTime.FromFileTimeUtc(create) : null;
                    var next = Marshal.ReadInt32(entry, 0);
                    if (next <= 0) break;
                    offset += next;
                    if (offset >= size) break;
                }
                return map;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return map;
    }

    /// <summary>Full image path for a PID via SystemProcessIdInformation, mapped from NT device to drive letter.</summary>
    private static string? ImageByPid(uint pid, IReadOnlyList<(string Device, string Drive)> devices)
    {
        const int chars = 1024;
        var info = Marshal.AllocHGlobal(24);
        var text = Marshal.AllocHGlobal(chars * 2);
        try
        {
            Marshal.WriteInt64(info, 0, pid);
            Marshal.WriteInt16(info, 8, 0);
            Marshal.WriteInt16(info, 10, unchecked((short)(chars * 2)));
            Marshal.WriteInt32(info, 12, 0);
            Marshal.WriteIntPtr(info, 16, text);
            if (Native.NtQuerySystemInformation(Native.SystemProcessIdInformation, info, 24, out _) != 0) return null;
            var len = (ushort)Marshal.ReadInt16(info, 8) / 2;
            if (len == 0) return null;
            var nt = Marshal.PtrToStringUni(text, len);
            foreach (var (device, drive) in devices)
                if (nt.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive + nt[device.Length..];
            return null; // not on a lettered volume: leave unknown rather than store a device path
        }
        finally
        {
            Marshal.FreeHGlobal(text);
            Marshal.FreeHGlobal(info);
        }
    }

    private static List<(string Device, string Drive)> DeviceMap()
    {
        var list = new List<(string, string)>();
        var buf = new char[512];
        foreach (var d in "CDEFGHIJKLMNOPQRSTUVWXYZAB")
        {
            var drive = d + ":";
            var n = Native.QueryDosDeviceW(drive, buf, buf.Length);
            if (n == 0) continue;
            var target = new string(buf, 0, Array.IndexOf(buf, '\0') is var z and >= 0 ? z : (int)n);
            if (target.Length > 0) list.Add((target, drive));
        }
        return list;
    }

    private static void ReadTable(bool tcp, int af, List<ListenerRowRaw> rows, List<string> errors)
    {
        var size = 0;
        var tableClass = tcp ? Native.TCP_TABLE_OWNER_PID_LISTENER : Native.UDP_TABLE_OWNER_PID;
        uint rc = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            rc = tcp ? Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, tableClass, 0)
                     : Native.GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, tableClass, 0);
            if (rc != Native.ERROR_INSUFFICIENT_BUFFER && rc != 0) break;
            size += 4096;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                rc = tcp ? Native.GetExtendedTcpTable(buf, ref size, false, af, tableClass, 0)
                         : Native.GetExtendedUdpTable(buf, ref size, false, af, tableClass, 0);
                if (rc == Native.ERROR_INSUFFICIENT_BUFFER) continue;
                if (rc != 0) break;
                Parse(buf, tcp, af, rows);
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        errors.Add($"{(tcp ? "TCP" : "UDP")}{(af == Native.AF_INET ? "v4" : "v6")} table error {rc}");
    }

    private static void Parse(IntPtr buf, bool tcp, int af, List<ListenerRowRaw> rows)
    {
        var count = Marshal.ReadInt32(buf);
        var v6 = af == Native.AF_INET6;
        // Row sizes: TCP v4 24, TCP v6 56, UDP v4 12, UDP v6 28. Rows start after the DWORD count.
        var rowSize = tcp ? (v6 ? 56 : 24) : (v6 ? 28 : 12);
        var proto = tcp ? "tcp" : "udp";
        var family = v6 ? "ipv6" : "ipv4";
        for (var i = 0; i < count && rows.Count < RowCap * 4; i++)
        {
            var row = buf + 4 + i * rowSize;
            byte[] addr;
            int portRaw;
            uint pid;
            if (!v6)
            {
                var offset = tcp ? 4 : 0; // TCP rows start with dwState
                addr = BitConverter.GetBytes(Marshal.ReadInt32(row, offset));
                portRaw = Marshal.ReadInt32(row, offset + 4);
                pid = (uint)Marshal.ReadInt32(row, tcp ? 20 : 8);
            }
            else
            {
                addr = new byte[16];
                Marshal.Copy(row, addr, 0, 16);
                portRaw = Marshal.ReadInt32(row, 20);
                pid = (uint)Marshal.ReadInt32(row, tcp ? 52 : 24);
            }
            var port = ((portRaw & 0xFF) << 8) | ((portRaw >> 8) & 0xFF);
            rows.Add(new ListenerRowRaw(proto, family, addr, port, pid));
        }
    }

    private static ProcessRaw QueryProcess(uint pid)
    {
        if (pid is 0 or 4) return new ProcessRaw(null, null, false);
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return new ProcessRaw(null, null, Marshal.GetLastWin32Error() == 5);
        try
        {
            string? image = null;
            var buf = new char[1024];
            var len = buf.Length;
            if (Native.QueryFullProcessImageNameW(h, 0, buf, ref len)) image = new string(buf, 0, len);
            DateTime? start = Native.GetProcessTimes(h, out var creation, out _, out _, out _) && creation > 0
                ? DateTime.FromFileTimeUtc(creation) : null;
            return new ProcessRaw(image, start, image is null);
        }
        finally { Native.CloseHandle(h); }
    }

    internal override CollectorResult Normalize(ListenersRaw raw)
    {
        var facts = new FactSet("Windows network tables");
        var merged = new Dictionary<string, (JsonObject Value, string Display)>(StringComparer.Ordinal);
        var unknown = 0;
        var truncated = false;
        foreach (var r in raw.Rows.OrderBy(r => r.Protocol, StringComparer.Ordinal).ThenBy(r => r.Port).ThenBy(r => r.Family, StringComparer.Ordinal))
        {
            var scope = Scope(r.Family, r.Address);
            raw.Processes.TryGetValue(r.Pid, out var proc);
            string? image = null;
            var system = r.Pid == 4;
            if (proc is { ImagePath: { } path } && !(proc.StartUtc is { } st && st > raw.SnapshotUtc))
                image = _roots.Normalize(path);
            if (r.Pid == 0) image = null;
            system |= PathRoots.IsUnderWindows(image);
            if (image is null && !system) unknown++;

            var identity = string.Join('|', r.Family, scope, system && image is null ? "system" : image ?? "unknown");
            var subject = $"net.listener.{r.Protocol}.{r.Port}.{Subjects.Hash8(identity)}";
            if (merged.TryGetValue(subject, out var existing))
            {
                existing.Value["bindCount"] = (J.Long(existing.Value["bindCount"]) ?? 1) + 1;
                continue;
            }
            if (merged.Count >= RowCap) { truncated = true; continue; }
            var owner = image is not null ? PathRoots.FileName(image) : system ? "Windows (System)" : "An unidentified process";
            merged[subject] = (new JsonObject
            {
                ["protocol"] = r.Protocol,
                ["family"] = r.Family,
                ["port"] = r.Port,
                ["scope"] = scope,
                ["pid"] = r.Pid,
                ["processStartedAt"] = image is not null ? Text.IsoOrNull(proc?.StartUtc) : null,
                ["image"] = image,
                ["imageKnown"] = image is not null,
                ["isSystem"] = system,
                ["bindCount"] = 1,
            }, $"{owner} {(r.Protocol == "tcp" ? "listens on TCP" : "uses UDP")} port {r.Port.ToString(CultureInfo.InvariantCulture)} {ScopeText(scope)}.");
        }
        foreach (var (subject, (value, display)) in merged) facts.Add(subject, value, display);

        var tcp = merged.Values.Count(v => J.Str(v.Value["protocol"]) == "tcp");
        facts.Add("net.listeners.summary", new JsonObject
        {
            ["tcpCount"] = tcp,
            ["udpCount"] = merged.Count - tcp,
            ["wildcardTcpCount"] = merged.Values.Count(v => J.Str(v.Value["protocol"]) == "tcp" && J.Str(v.Value["scope"]) == "wildcard"),
            ["unknownOwnerRows"] = unknown,
            ["truncated"] = truncated,
        }, $"{tcp} TCP listeners and {merged.Count - tcp} UDP endpoints found.");

        var reasons = new List<string>(raw.TableErrors);
        if (unknown > 0) reasons.Add($"{unknown} owners could not be identified without administrator rights.");
        if (truncated) reasons.Add($"Capped at {RowCap} endpoints.");
        var status = raw.TableErrors.Count == 4 ? "failed" : reasons.Count == 0 ? "complete" : "partial";
        return facts.Result(status, reasons.Count == 0 ? null : string.Join(" ", reasons));
    }

    internal static string Scope(string family, byte[] a)
    {
        if (family == "ipv4")
            return a.All(b => b == 0) ? "wildcard" : a[0] == 127 ? "loopback" : "specific";
        if (a.All(b => b == 0)) return "wildcard";
        if (a.Take(15).All(b => b == 0) && a[15] == 1) return "loopback";
        // IPv4-mapped ::ffff:127.x
        if (a.Take(10).All(b => b == 0) && a[10] == 0xFF && a[11] == 0xFF && a[12] == 127) return "loopback";
        return "specific";
    }

    private static string ScopeText(string scope) => scope switch
    {
        "wildcard" => "on all networks",
        "loopback" => "on this PC only",
        _ => "on one network address",
    };
}
