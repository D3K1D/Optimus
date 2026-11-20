using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Actions.Broker.Reads;

namespace OptimizationTool.Windows.Tests.Actions.Broker;

/// <summary>
/// The administrator-check adapters (m9 §17.2) against fake WMI rows: nothing on this PC is queried. Every item they
/// return must fit its catalog field list exactly; "nothing found" (ok, complete) and "could not read" stay different.
/// </summary>
public sealed class ReadAdapterTests
{
    private sealed class FakeWmi : IReadWmi
    {
        /// <summary>Rows per class name (the word after FROM); a ReadWmiException to throw instead.</summary>
        public readonly Dictionary<string, object> Classes = new(StringComparer.Ordinal);
        public Func<string, string, IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>>? OnCall;
        public readonly List<string> Queries = [];
        public readonly List<string> Calls = [];

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string ns, string wql, IReadOnlyList<string> props, int cap)
        {
            Queries.Add(wql);
            var cls = wql[(wql.LastIndexOf(" FROM ", StringComparison.Ordinal) + 6)..].Trim();
            if (!Classes.TryGetValue(cls, out var v)) throw new ReadWmiException(ReadWmiError.NoClass, "0x80041010");
            if (v is ReadWmiException ex) throw ex;
            // The seam returns only the named properties.
            return [.. ((IEnumerable<Dictionary<string, object?>>)v).Take(cap).Select(r => (IReadOnlyDictionary<string, object?>)props.ToDictionary(p => p, p => r.GetValueOrDefault(p), StringComparer.OrdinalIgnoreCase))];
        }

        public IReadOnlyDictionary<string, object?> Call(string ns, string className, string keyProperty, string keyValue, string method, IReadOnlyDictionary<string, object?> args, IReadOnlyList<string> outProps)
        {
            Calls.Add(method);
            Assert.Contains(method, WindowsReadWmi.AllowedMethods[className]);
            return OnCall?.Invoke(keyValue, method, args) ?? throw new ReadWmiException(ReadWmiError.Other, "0x80004005");
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Dmtf(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".000000+000";

    private static void Fits(string id, AdminReadOutput o) => Assert.All(o.Items, i => Assert.True(AdminChecks.Conforms(AdminChecks.Find(id)!, i), i.ToJsonString()));

    // ---------------------------------------------------------------- security.defender_history

    private static FakeWmi Defender(int detections, int daysApart = 1, string mode = "Normal")
    {
        var w = new FakeWmi();
        w.Classes["MSFT_MpThreatDetection"] = Enumerable.Range(0, detections).Select(i => new Dictionary<string, object?>
        {
            ["ThreatID"] = (long)(i % 3 + 1), ["InitialDetectionTime"] = Dmtf(Now.AddDays(-i * daysApart).AddMinutes(-7).AddSeconds(-13)),
            ["CleaningActionID"] = (byte)2, ["ActionSuccess"] = true, ["DetectionSourceTypeID"] = (byte)3,
            ["Resources"] = new object[] { @"file:_C:\Users\someone\Downloads\evil.exe", @"process:_pid:4242,ProcessStart:1,C:\x.exe" },
        }).ToList();
        w.Classes["MSFT_MpThreat"] = new List<Dictionary<string, object?>>
        {
            new() { ["ThreatID"] = 1L, ["ThreatName"] = "Trojan:Win32/Wacatac.B!ml", ["CategoryID"] = (byte)8, ["SeverityID"] = (byte)5 },
            new() { ["ThreatID"] = 2L, ["ThreatName"] = "PUA:Win32/Bundler\u0007", ["CategoryID"] = (byte)27, ["SeverityID"] = (byte)1 },
            new() { ["ThreatID"] = 3L, ["ThreatName"] = "Behavior:Win32/Mystery.A", ["CategoryID"] = (byte)250, ["SeverityID"] = (byte)9 },
        };
        w.Classes["MSFT_MpComputerStatus"] = new List<Dictionary<string, object?>> { new() { ["AMRunningMode"] = mode } };
        return w;
    }

    [Fact]
    public void Defender_history_returns_the_window_newest_first_with_counts_never_paths()
    {
        var w = Defender(10);
        var o = new DefenderHistoryRead(w).Read(new AdminReadInput(5, Now), CancellationToken.None);
        Assert.Equal("ok", o.State);
        Assert.Equal(5, o.Total); // days 0..4 within the 5-day window
        Assert.True(o.Complete);
        Fits(AdminChecks.DefenderHistory, o);
        var first = o.Items[0];
        Assert.Equal("Trojan:Win32/Wacatac.B!ml", first["threatName"]!.GetValue<string>());
        Assert.Equal(("trojan", "severe", "quarantine", "real_time"), (first["category"]!.GetValue<string>(), first["severity"]!.GetValue<string>(),
            first["action"]!.GetValue<string>(), first["foundBy"]!.GetValue<string>()));
        Assert.Equal("2026-11-19T11:52Z", first["detectedAt"]!.GetValue<string>()); // to the minute
        Assert.Equal(2, first["affectedItems"]!.GetValue<int>());
        Assert.Equal("PUA:Win32/Bundler", o.Items[1]["threatName"]!.GetValue<string>()); // control characters cleaned
        Assert.Equal(("unknown", "unknown"), (o.Items[2]["category"]!.GetValue<string>(), o.Items[2]["severity"]!.GetValue<string>()));
        var all = string.Join("", o.Items.Select(i => i.ToJsonString()));
        Assert.DoesNotContain("evil.exe", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", all, StringComparison.Ordinal);
        Assert.All(w.Queries, q => Assert.StartsWith("SELECT ", q));
        Assert.DoesNotContain(w.Queries, q => q.Contains('*'));
    }

    [Fact]
    public void Defender_history_keeps_the_newest_50_and_says_more_matched()
    {
        var o = new DefenderHistoryRead(Defender(80, daysApart: 0)).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal((50, 80, false), (o.Items.Count, o.Total, o.Complete));
    }

    [Fact]
    public void Defender_history_nothing_found_differs_from_could_not_read()
    {
        var none = new DefenderHistoryRead(Defender(0)).Read(new AdminReadInput(30, Now), CancellationToken.None);
        Assert.Equal(("ok", 0, true, (string?)null), (none.State, none.Items.Count, none.Complete, none.Code));

        var passive = new DefenderHistoryRead(Defender(0, mode: "Passive Mode")).Read(new AdminReadInput(30, Now), CancellationToken.None);
        Assert.Equal(("ok", false, "defender_not_primary"), (passive.State, passive.Complete, passive.Code));

        var missing = new FakeWmi(); // no Defender classes at all
        Assert.Equal(("unsupported", "defender_unavailable"), Pair(new DefenderHistoryRead(missing).Read(new AdminReadInput(30, Now), CancellationToken.None)));

        var denied = Defender(3);
        denied.Classes["MSFT_MpThreatDetection"] = new ReadWmiException(ReadWmiError.AccessDenied, "0x80041003");
        Assert.Equal(("failed", "access_denied"), Pair(new DefenderHistoryRead(denied).Read(new AdminReadInput(30, Now), CancellationToken.None)));

        var broken = Defender(3);
        broken.Classes["MSFT_MpThreat"] = new ReadWmiException(ReadWmiError.Other, "0x80004005");
        Assert.Equal(("failed", "read_error"), Pair(new DefenderHistoryRead(broken).Read(new AdminReadInput(30, Now), CancellationToken.None)));
    }

    private static (string, string?) Pair(AdminReadOutput o) => (o.State, o.Code);

    // ---------------------------------------------------------------- security.encryption

    private static FakeWmi Volumes()
    {
        var w = new FakeWmi();
        w.Classes["Win32_EncryptableVolume"] = new List<Dictionary<string, object?>>
        {
            new() { ["DeviceID"] = @"\\?\Volume{c}\", ["DriveLetter"] = "C:", ["ProtectionStatus"] = 1u, ["ConversionStatus"] = 1u, ["EncryptionMethod"] = 6u },
            new() { ["DeviceID"] = @"\\?\Volume{d}\", ["DriveLetter"] = "D:", ["ProtectionStatus"] = 0u, ["ConversionStatus"] = 0u, ["EncryptionMethod"] = 0u },
            new() { ["DeviceID"] = @"\\?\Volume{r}\", ["DriveLetter"] = null, ["ProtectionStatus"] = 0u, ["ConversionStatus"] = 0u, ["EncryptionMethod"] = 0u },
        };
        w.OnCall = (volume, method, args) => method switch
        {
            "GetConversionStatus" => new Dictionary<string, object?> { ["ReturnValue"] = 0u, ["ConversionStatus"] = volume.Contains("{c}") ? 1u : 0u, ["EncryptionPercentage"] = volume.Contains("{c}") ? 100u : 0u },
            "GetKeyProtectors" => new Dictionary<string, object?> { ["ReturnValue"] = 0u, ["VolumeKeyProtectorID"] = volume.Contains("{c}") ? new object[] { "{AAAA-1111}", "{BBBB-2222}", "{CCCC-3333}" } : Array.Empty<object>() },
            _ => new Dictionary<string, object?> { ["ReturnValue"] = 0u, ["KeyProtectorType"] = args["VolumeKeyProtectorID"] switch { "{AAAA-1111}" => 1u, "{BBBB-2222}" => 3u, _ => 1u } },
        };
        return w;
    }

    [Fact]
    public void Encryption_lists_lettered_volumes_with_protector_types_never_ids()
    {
        var w = Volumes();
        var o = new EncryptionRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal(("ok", 2, 2, true), (o.State, o.Items.Count, o.Total, o.Complete));
        Fits(AdminChecks.Encryption, o);
        var c = o.Items[0];
        Assert.Equal(("C:", "on", "fully_encrypted", 100L, "xts_aes_128"), (c["drive"]!.GetValue<string>(), c["protection"]!.GetValue<string>(),
            c["conversion"]!.GetValue<string>(), c["percent"]!.GetValue<long>(), c["method"]!.GetValue<string>()));
        Assert.Equal(["tpm", "recovery_password"], c["protectorTypes"]!.AsArray().Select(x => x!.GetValue<string>()));
        var all = string.Join("", o.Items.Select(i => i.ToJsonString()));
        Assert.DoesNotContain("AAAA", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Volume{", all, StringComparison.Ordinal);
        Assert.Equal(("D:", "off"), (o.Items[1]["drive"]!.GetValue<string>(), o.Items[1]["protection"]!.GetValue<string>()));
    }

    [Fact]
    public void Encryption_missing_class_is_unsupported_and_a_failed_method_is_incomplete()
    {
        Assert.Equal(("unsupported", "bitlocker_unavailable"), Pair(new EncryptionRead(new FakeWmi()).Read(new AdminReadInput(null, Now), CancellationToken.None)));
        var denied = new FakeWmi();
        denied.Classes["Win32_EncryptableVolume"] = new ReadWmiException(ReadWmiError.AccessDenied, "0x80041003");
        Assert.Equal(("failed", "access_denied"), Pair(new EncryptionRead(denied).Read(new AdminReadInput(null, Now), CancellationToken.None)));
        var empty = new FakeWmi();
        empty.Classes["Win32_EncryptableVolume"] = new List<Dictionary<string, object?>>();
        var none = new EncryptionRead(empty).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal(("ok", 0, true), (none.State, none.Items.Count, none.Complete));
        var w = Volumes();
        w.OnCall = (_, _, _) => throw new ReadWmiException(ReadWmiError.AccessDenied, "0x80041003");
        var partial = new EncryptionRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal(("ok", false), (partial.State, partial.Complete));
        Assert.Null(partial.Items[0]["percent"]);
        Assert.Empty(partial.Items[0]["protectorTypes"]!.AsArray());
        Fits(AdminChecks.Encryption, partial);
    }

    // ---------------------------------------------------------------- storage.health

    private static FakeWmi Disks()
    {
        var w = new FakeWmi();
        w.Classes["MSFT_PhysicalDisk"] = new List<Dictionary<string, object?>>
        {
            new() { ["DeviceId"] = "1", ["FriendlyName"] = "USB Stick", ["Model"] = "Stick", ["SerialNumber"] = "SER-123" },
            new() { ["DeviceId"] = "0", ["FriendlyName"] = @"C:\Users\someone renamed this", ["Model"] = "Fast NVMe 1TB", ["SerialNumber"] = "SER-456" },
        };
        w.Classes["MSFT_StorageReliabilityCounter"] = new List<Dictionary<string, object?>>
        {
            new() { ["DeviceId"] = "0", ["Temperature"] = (ushort)41, ["Wear"] = (byte)3, ["ReadErrorsTotal"] = null, ["WriteErrorsTotal"] = 0UL, ["PowerOnHours"] = 1200u },
        };
        return w;
    }

    [Fact]
    public void Storage_health_joins_counters_to_disks_and_says_when_a_disk_has_none()
    {
        var w = Disks();
        var o = new StorageHealthRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal(("ok", 2, false), (o.State, o.Items.Count, o.Complete)); // the USB disk has no counters
        Fits(AdminChecks.StorageHealth, o);
        var nvme = o.Items[0];
        Assert.Equal((0L, "Fast NVMe 1TB", true, 41L, 3L), (nvme["disk"]!.GetValue<long>(), nvme["model"]!.GetValue<string>(), nvme["counters"]!.GetValue<bool>(),
            nvme["temperatureC"]!.GetValue<long>(), nvme["wearPercent"]!.GetValue<long>()));
        Assert.Null(nvme["readErrors"]); // not reported stays null, never 0
        Assert.Equal(0L, nvme["writeErrors"]!.GetValue<long>());
        var usb = o.Items[1];
        Assert.False(usb["counters"]!.GetValue<bool>());
        Assert.Null(usb["temperatureC"]);
        Assert.DoesNotContain("SER-", string.Join("", o.Items.Select(i => i.ToJsonString())), StringComparison.Ordinal);
        Assert.DoesNotContain(w.Queries, q => q.Contains("Serial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Storage_health_denied_counters_fail_and_a_missing_class_is_unsupported()
    {
        var w = Disks();
        w.Classes["MSFT_StorageReliabilityCounter"] = new ReadWmiException(ReadWmiError.AccessDenied, "0x80041003");
        Assert.Equal(("failed", "access_denied"), Pair(new StorageHealthRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None)));
        Assert.Equal(("unsupported", "storage_unavailable"), Pair(new StorageHealthRead(new FakeWmi()).Read(new AdminReadInput(null, Now), CancellationToken.None)));
        var empty = new FakeWmi();
        empty.Classes["MSFT_PhysicalDisk"] = new List<Dictionary<string, object?>>();
        empty.Classes["MSFT_StorageReliabilityCounter"] = new List<Dictionary<string, object?>>();
        var none = new StorageHealthRead(empty).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal(("ok", 0, true), (none.State, none.Items.Count, none.Complete));
    }

    [Fact]
    public void Storage_health_names_the_disk_from_Model_only_never_the_editable_FriendlyName()
    {
        var w = Disks();
        var o = new StorageHealthRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal("Fast NVMe 1TB", o.Items[0]["model"]!.GetValue<string>());
        Assert.DoesNotContain("someone", string.Join("", o.Items.Select(i => i.ToJsonString())), StringComparison.Ordinal);
        Assert.DoesNotContain(w.Queries, q => q.Contains("FriendlyName", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- review item 6: every query and array cap reaches coverage

    private static List<Dictionary<string, object?>> Rows(int n, Func<int, Dictionary<string, object?>> make) => [.. Enumerable.Range(0, n).Select(make)];

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Encryption_volume_rows_at_the_cap(int rows, bool complete)
    {
        var w = new FakeWmi { OnCall = (_, m, _) => m == "GetKeyProtectors"
            ? new Dictionary<string, object?> { ["ReturnValue"] = 0u, ["VolumeKeyProtectorID"] = Array.Empty<object>() }
            : new Dictionary<string, object?> { ["ReturnValue"] = 0u, ["ConversionStatus"] = 0u, ["EncryptionPercentage"] = 0u } };
        // Only the first is lettered; the rest have no letter (filtered AFTER the cap: their count still matters).
        w.Classes["Win32_EncryptableVolume"] = Rows(rows, i => new() { ["DeviceID"] = $"v{i}", ["DriveLetter"] = i == 0 ? "C:" : null, ["ProtectionStatus"] = 0u, ["ConversionStatus"] = 0u, ["EncryptionMethod"] = 0u });
        var o = new EncryptionRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal((1, complete), (o.Items.Count, o.Complete));
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Encryption_protector_ids_at_the_cap(int ids, bool complete)
    {
        var w = Volumes();
        w.Classes["Win32_EncryptableVolume"] = new List<Dictionary<string, object?>> { new() { ["DeviceID"] = @"\\?\Volume{c}\", ["DriveLetter"] = "C:", ["ProtectionStatus"] = 1u, ["ConversionStatus"] = 1u, ["EncryptionMethod"] = 6u } };
        var inner = w.OnCall!;
        w.OnCall = (v, m, a) => m == "GetKeyProtectors"
            ? new Dictionary<string, object?> { ["ReturnValue"] = 0u, ["VolumeKeyProtectorID"] = Enumerable.Range(0, ids).Select(i => (object)$"{{ID-{i}}}").ToArray() }
            : inner(v, m, a);
        var o = new EncryptionRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal(complete, o.Complete);
        Assert.Equal(16, w.Calls.Count(c => c == "GetKeyProtectorType")); // never more than the cap looked at
    }

    [Theory]
    [InlineData(64, 64, true)]
    [InlineData(65, 64, false)]
    [InlineData(64, 65, false)]
    public void Storage_rows_at_the_cap(int disks, int counters, bool complete)
    {
        var w = new FakeWmi();
        // Disks 0..n-1 but only disk 0 is real (listed once), the rest are unnumbered rows; every counter row is for disk 0.
        w.Classes["MSFT_PhysicalDisk"] = Rows(disks, i => new() { ["DeviceId"] = i == 0 ? "0" : "x", ["Model"] = "Disk" });
        w.Classes["MSFT_StorageReliabilityCounter"] = Rows(counters, _ => new() { ["DeviceId"] = "0", ["Temperature"] = 40 });
        var o = new StorageHealthRead(w).Read(new AdminReadInput(null, Now), CancellationToken.None);
        Assert.Equal((1, complete), (o.Items.Count, o.Complete));
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void Defender_rows_at_the_cap(int rows, bool complete)
    {
        // Every row is older than the window, so only the row cap can make the result incomplete.
        var w = Defender(0);
        w.Classes["MSFT_MpThreatDetection"] = Rows(rows, _ => new()
        {
            ["ThreatID"] = 1L, ["InitialDetectionTime"] = Dmtf(Now.AddDays(-200)), ["CleaningActionID"] = 2, ["ActionSuccess"] = true, ["DetectionSourceTypeID"] = 3,
        });
        var o = new DefenderHistoryRead(w).Read(new AdminReadInput(30, Now), CancellationToken.None);
        Assert.Equal((0, complete), (o.Items.Count, o.Complete));
    }

    [Fact]
    public void Only_bitlocker_getters_are_allowed_methods()
    {
        Assert.Equal(["Win32_EncryptableVolume"], WindowsReadWmi.AllowedMethods.Keys);
        Assert.Equal(["GetConversionStatus", "GetKeyProtectors", "GetKeyProtectorType"], WindowsReadWmi.AllowedMethods["Win32_EncryptableVolume"]);
        using var real = new WindowsReadWmi();
        Assert.Throws<InvalidOperationException>(() => real.Call("x", "Win32_EncryptableVolume", "DeviceID", "v", "Encrypt", new Dictionary<string, object?>(), []));
        Assert.Throws<InvalidOperationException>(() => real.Call("x", "Win32_Process", "Handle", "1", "Create", new Dictionary<string, object?>(), []));
    }
}
