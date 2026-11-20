using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>m9 review R4: the one never-store check, shared by the inventory, the startup evidence and the check-up entries.</summary>
public sealed class NeverStoreTests
{
    [Theory]
    [InlineData("Viewer fe80::1%12")]
    [InlineData("Viewer ::ffff:1.2.3.4")]
    [InlineData("Viewer fe80:0:1")]
    [InlineData("Office VK7JG-NPHTM-C97JM-9MPGT-3V66T")]
    [InlineData("Licence ABCD-1234-EF56-7890")]
    [InlineData("Disk WD-WCC4E1234567")]
    [InlineData("Disk 5A3F2C1D-0B9E-4A7C-8D6F-1E2B3C4D5E6F")]
    [InlineData("Viewer 192.168.1.20")]
    public void Names_with_forbidden_content_are_rejected_and_never_reach_a_checkup_entry(string name)
    {
        Assert.True(NeverStore.RejectsName(name));
        Assert.Null(CheckupPolicy.CleanEntryName(name, []));
    }

    [Theory]
    [InlineData("Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810")]
    [InlineData("7-Zip 24.08 (x64)")]
    [InlineData("NVIDIA Graphics Driver 566.36")]
    [InlineData("Microsoft-Windows-Defender")]
    public void Plain_names_are_kept(string name) => Assert.False(NeverStore.RejectsName(name));

    [Theory]
    [InlineData("10.0.26100.2")]
    [InlineData("1.2.3-beta+build.5")]
    [InlineData("24.08")]
    [InlineData("2.1.0.1234")]
    [InlineData("1.0.0.abcd")]
    public void Version_syntax_is_kept(string version)
    {
        Assert.False(NeverStore.RejectsVersion(version));
        Assert.Equal(version, CheckupPolicy.CleanVersion(version, []));
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("::ffff:1.2.3.4")]
    [InlineData("1:2:3")]
    [InlineData("3C:7C:3F:1A:2B:4D")]
    [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T")]
    [InlineData("1.0-WCC4E1234567")]
    public void Versions_with_forbidden_content_are_rejected(string version) => Assert.True(NeverStore.RejectsVersion(version));
}

/// <summary>m9 review S4: dotted MAC, IPv4 with a port or other text, and a serial run behind a file extension.</summary>
public sealed class NeverStoreS4Tests
{
    [Theory]
    [InlineData("Switch 0011.2233.4455")]
    [InlineData("Switch aabb.CCDD.eeff")]
    [InlineData("Proxy 192.168.1.20:8080")]
    [InlineData("192.168.1.20.exe")]
    [InlineData("AB12CD34EF56GH.exe")]
    public void Names_are_rejected(string name) => Assert.True(NeverStore.RejectsName(name));

    [Theory]
    [InlineData("vcredist_x64.exe")]
    [InlineData("MicrosoftEdgeUpdate.exe")]
    [InlineData("7z2408-x64.exe")]
    [InlineData("Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810")]
    [InlineData("7-Zip 24.08 (x64)")]
    [InlineData("NVIDIA Graphics Driver 566.36")]
    public void Ordinary_file_and_program_names_are_kept(string name) => Assert.False(NeverStore.RejectsName(name));

    [Theory]
    [InlineData("0a11.2233.4455")]                // review S4b: an all-decimal three-group value is a version
    [InlineData("AABB.ccdd.EEFF")]
    [InlineData("192.168.1.20:8080")]
    [InlineData("1.2.3.4 beta")]
    [InlineData("1.2.3.4-192.168.1.20")]
    [InlineData("v192.168.1.20")]
    public void Versions_are_rejected(string version) => Assert.True(NeverStore.RejectsVersion(version));

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("10.0.26100.2")]
    [InlineData("14.0.24215.1")]
    [InlineData("1.2.3.4.5")]
    public void Plain_versions_are_kept(string version) => Assert.False(NeverStore.RejectsVersion(version));
}


/// <summary>
/// m9 review S4b: a dotted MAC followed by punctuation (a trailing period, a file extension) is still a dotted MAC in names,
/// publishers and model fields; a version keeps a value that is wholly a plain dotted decimal number (<c>1234.5678.9012</c>).
/// </summary>
public sealed class NeverStoreS4bTests
{
    [Theory]
    [InlineData("Adapter 0011.2233.4455.")]
    [InlineData("0011.2233.4455.exe")]
    [InlineData("Switch aabb.CCDD.eeff, port 2")]
    [InlineData("Switch (0011.2233.4455)")]
    [InlineData("Switch 1234.5678.9012")]
    [InlineData("Switch 0011.2233.4455.6677")]
    [InlineData("Switch.0011.2233.4455")]
    public void Names_and_models_are_rejected(string s)
    {
        Assert.True(NeverStore.RejectsName(s));
        Assert.True(NeverStore.RejectsModel(s));
    }

    [Theory]
    [InlineData("1234.5678.9012")]
    [InlineData("0011.2233.4455")]
    [InlineData("2024.0101.1234.5678")]
    [InlineData("10.0.26100.2")]
    [InlineData("14.0.24215.1")]
    public void Plain_dotted_decimal_versions_are_kept(string version)
    {
        Assert.False(NeverStore.RejectsVersion(version));
        Assert.Equal(version, CheckupPolicy.CleanVersion(version, []));
    }

    [Theory]
    [InlineData("00a1.2233.4455")]
    [InlineData("0011.2233.4455.")]
    [InlineData("0011.2233.4455.exe")]
    [InlineData("v0011.2233.4455")]
    [InlineData("1.0 aabb.ccdd.eeff")]
    public void Versions_are_rejected(string version)
    {
        Assert.True(NeverStore.RejectsVersion(version));
        Assert.Null(CheckupPolicy.CleanVersion(version, []));
    }

    [Theory]
    [InlineData("Contoso Tool 1.2.3.")]
    [InlineData("WDC WD10EZEX-08WN4A0")]
    [InlineData("abcd.ef01 tool")]
    [InlineData("Contoso 12345.6789.0123")]
    public void Neighbouring_shapes_are_kept_as_models(string s) => Assert.False(NeverStore.RejectsModel(s));
}
