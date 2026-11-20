using OptimizationTool.Windows.Actions.Protection;
using Prot = OptimizationTool.Windows.Actions.Protection.Protection;

namespace OptimizationTool.Windows.Tests.Actions.Protection;

/// <summary>contracts/m9-ai-powers.md §3.2 protected lists and §3.4 hardware limits, with hostile spellings. Pure functions.</summary>
public class ProtectedTargetsTests
{
    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("EXPLORER.EXE")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData("  lsass.exe  ")]
    [InlineData("\"C:\\Windows\\System32\\csrss.exe\"")]
    [InlineData("winlogon")]
    [InlineData("svchost.exe")]
    [InlineData("dwm.exe")]
    [InlineData("MsMpEng.exe")]
    [InlineData("SecurityHealthSystray.exe")]
    [InlineData("ekrn.exe")]
    [InlineData("OptimizationTool.Host.exe")]
    [InlineData("optimizationtool.desktop.EXE")]
    [InlineData("Optimus.exe")]
    [InlineData("csr\u200Bss.exe")] // a zero-width character does not hide it
    [InlineData("")]
    [InlineData(null)]
    public void Processes_that_are_protected(string? image)
    {
        var p = ProtectedTargets.Process(image);
        Assert.NotNull(p);
        Assert.Equal(Prot.ProtectedCode, p!.Code);
        Assert.False(string.IsNullOrWhiteSpace(p.Reason));
    }

    [Theory]
    [InlineData("notepad.exe", null)]
    [InlineData(@"C:\Users\pat\AppData\Local\Discord\app-1.0\Discord.exe", "Discord Inc.")]
    [InlineData("chrome.exe", "Google LLC")]
    [InlineData("msix-helper.exe", "MSIX Packaging Tool")] // "msi" is a whole-word publisher match only
    [InlineData("explorer2.exe", null)]
    public void Ordinary_processes_are_not(string image, string? publisher) =>
        Assert.Null(ProtectedTargets.Process(image, publisher));

    [Theory]
    [InlineData("updater.exe", "ESET, spol. s r.o.")]
    [InlineData("helper.exe", "  BITDEFENDER   SRL ")]
    [InlineData("agent.exe", "Malwarebytes Inc.")]
    [InlineData("sync.exe", "Acronis International GmbH")]
    [InlineData("tray.exe", "Kaseya US LLC")]
    public void Renamed_files_of_security_backup_and_management_publishers_are_protected(string image, string publisher) =>
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Process(image, publisher)!.Code);

    [Theory]
    [InlineData("MSI Afterburner", null, null)]
    [InlineData("  msi   AFTERBURNER ", null, null)]
    [InlineData("Helper", @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.EXE", null)]
    [InlineData("RTSS", null, null)]
    [InlineData("ThrottleStop", null, null)]
    [InlineData("Fan Control", null, null)]
    [InlineData("SpeedFan", null, null)]
    [InlineData("Armoury Crate", null, null)]
    [InlineData("ArmouryCrate.UserSessionHelper", null, null)]
    [InlineData("AMD Ryzen Master", null, null)]
    [InlineData("Intel(R) XTU", @"C:\Program Files\Intel\XTU\XtuService.exe", null)]
    [InlineData("iCUE", null, null)]
    [InlineData("NZXT CAM", null, null)]
    [InlineData("updater", @"C:\x\updater.exe", "EVGA Corporation")]               // renamed, same publisher
    [InlineData("tool", @"C:\x\tool.exe", "Micro-Star International CO., LTD.")]
    [InlineData("svc", null, "  corsair memory, INC. ")]
    public void Hardware_tuning_and_fan_tools_are_hardware_limit(string name, string? image, string? publisher)
    {
        var p = ProtectedTargets.StartupEntry(name, image, publisher);
        Assert.NotNull(p);
        Assert.Equal(Prot.HardwareLimitCode, p!.Code);
        Assert.Equal(Prot.HardwareLimitCode, ProtectedTargets.Process(image ?? name + ".exe", publisher)?.Code ?? ProtectedTargets.StartupEntry(name, image, publisher)!.Code);
    }

    [Theory]
    [InlineData("Spotify", @"C:\Users\pat\AppData\Roaming\Spotify\Spotify.exe", "Spotify AB")]
    [InlineData("Discord", null, "Discord Inc.")]
    [InlineData("Steam", @"C:\Program Files (x86)\Steam\steam.exe", "Valve Corp.")]
    [InlineData("Microsoft Teams", null, "Microsoft Corporation")]
    [InlineData("OneDrive", null, "Microsoft Corporation")]
    public void Ordinary_startup_entries_are_allowed(string name, string? image, string? publisher) =>
        Assert.Null(ProtectedTargets.StartupEntry(name, image, publisher));

    [Theory]
    [InlineData("Optimus", @"C:\Program Files\Optimus\OptimizationTool.Desktop.exe")]
    [InlineData("Windows Security notification icon", @"%windir%\system32\SecurityHealthSystray.exe")]
    public void Startup_entries_for_Optimus_or_security_software_are_protected(string name, string image) =>
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.StartupEntry(name, image)!.Code);

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("windefend")]
    [InlineData(" wuauserv ")]
    [InlineData("RpcSs")]
    [InlineData("Schedule")]
    [InlineData("EventLog")]
    [InlineData("msiserver")]
    [InlineData("CryptSvc")]
    [InlineData("ProfSvc")]
    [InlineData("Power")]
    [InlineData("PlugPlay")]
    [InlineData("mpssvc")]
    public void Windows_services_are_protected(string service) =>
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Service(service)!.Code);

    [Fact]
    public void Drivers_security_backup_and_management_services_are_protected_and_tuning_services_are_hardware_limit()
    {
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Service("anydriver", kernelDriver: true)!.Code);
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Service("ekrn", "ESET Service", "ESET, spol. s r.o.")!.Code);
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Service("VeeamEndpointBackupSvc", null, "Veeam Software Group GmbH")!.Code);
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Service("IntuneManagementExtension", "Microsoft Intune Management Extension", "Microsoft Corporation")!.Code);
        Assert.Equal(Prot.HardwareLimitCode, ProtectedTargets.Service("XtuOcDriverService", "Intel(R) Extreme Tuning Utility")!.Code);
        Assert.Equal(Prot.HardwareLimitCode, ProtectedTargets.Service("ArmouryCrateService")!.Code);
        Assert.Null(ProtectedTargets.Service("SteamClientService", "Steam Client Service", "Valve Corp."));
        Assert.Null(ProtectedTargets.Service("Spooler2"));
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan")]
    [InlineData(@"\microsoft\windows\defrag\scheduleddefrag")]
    [InlineData(@"\\Microsoft\\\Windows\X")]
    [InlineData(@"  \MICROSOFT\Office\Telemetry ")]
    [InlineData(@"Microsoft\Windows\X")]
    [InlineData(@"/Microsoft/Windows/X")]
    [InlineData("")]
    public void Tasks_under_Microsoft_are_protected(string path) =>
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Task(path)!.Code);

    [Fact]
    public void Other_tasks_follow_what_they_run()
    {
        Assert.Null(ProtectedTargets.Task(@"\GoogleUpdateTaskMachineUA", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "Google LLC"));
        Assert.Null(ProtectedTargets.Task(@"\MicrosoftEdgeUpdateTaskMachineCore")); // a name that only starts with "Microsoft"
        Assert.Equal(Prot.HardwareLimitCode, ProtectedTargets.Task(@"\MSIAfterburner", @"C:\x\MSIAfterburner.exe")!.Code);
        Assert.Equal(Prot.ProtectedCode, ProtectedTargets.Task(@"\Avast Software\Overseer", @"C:\x\overseer.exe", "Avast Software s.r.o.")!.Code);
    }
}
