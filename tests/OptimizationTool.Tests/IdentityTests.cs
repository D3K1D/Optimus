using System.IO.Pipes;
using System.Security.Principal;
using OptimizationTool.Engine;
using OptimizationTool.Host;

namespace OptimizationTool.Tests;

/// <summary>
/// Another local user cannot be simulated in a unit test run, so the check function is tested directly and the
/// inspection path is tested against this process's own connection.
/// </summary>
public class IdentityTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    [Fact]
    public void SameUserAndSession_IsAllowed() => Assert.True(ClientIdentity.IsAllowed(Me, 1, Me, 1));

    [Fact]
    public void AnotherUser_IsRejected()
    {
        var other = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1002");
        Assert.False(ClientIdentity.IsAllowed(other, 1, Me, 1));
        Assert.False(ClientIdentity.IsAllowed(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), 1, Me, 1));
        Assert.False(ClientIdentity.IsAllowed(null, 1, Me, 1));
    }

    [Fact]
    public void SameUserInAnotherSession_IsRejected() => Assert.False(ClientIdentity.IsAllowed(Me, 2, Me, 1));

    [Fact]
    public async Task Inspect_ReadsThisProcessAsTheClient()
    {
        var name = "OptimizationTool.test.identity." + Guid.NewGuid().ToString("N");
        await using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, HostServer.CreateSecurity(Me));
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));

        var info = ClientIdentity.Inspect(server);
        var (user, session) = ClientIdentity.Current();
        Assert.Equal((uint)Environment.ProcessId, info.ProcessId);
        Assert.Equal(user, info.User);
        Assert.Equal(session, info.SessionId);
        Assert.True(ClientIdentity.IsAllowed(info.User, info.SessionId, user, session));
        Assert.EndsWith(".exe", info.ImagePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PipeOwner_IsThisProcessesDefaultOwner_SoElevatedClientsAcceptIt()
    {
        // CurrentUserOnly clients compare the pipe's owner with WindowsIdentity.Owner, which is BUILTIN\Administrators
        // in an elevated process; owning the pipe as the user made "Run as administrator" fail to connect.
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(me.Owner, HostServer.CreateSecurity(Me).GetOwner(typeof(SecurityIdentifier)));
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Assert.Equal(admins, HostServer.CreateSecurity(Me, admins).GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void PipeAcl_GrantsUserAndSystem_DeniesNetwork()
    {
        var rules = HostServer.CreateSecurity(Me).GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>().Select(r => ((SecurityIdentifier)r.IdentityReference, r.AccessControlType)).ToList();
        Assert.Contains((Me, System.Security.AccessControl.AccessControlType.Allow), rules);
        Assert.Contains((new SecurityIdentifier(WellKnownSidType.NetworkSid, null), System.Security.AccessControl.AccessControlType.Deny), rules);
        Assert.DoesNotContain(rules, r => r.Item1.IsWellKnown(WellKnownSidType.WorldSid));
    }
}

public class IdsTests
{
    [Fact]
    public void Ids_AreSha256OfNulJoinedParts_First16Hex()
    {
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("scan-1\0fixture.firewall\0firewall.profile.public"u8))[..16];
        Assert.Equal("ev-" + expected, Ids.Evidence("scan-1", "fixture.firewall", "firewall.profile.public"));
        Assert.Matches("^fnd-[0-9a-f]{16}$", Ids.Finding("scan-1", "fw.public_off", Ids.Fingerprint("fw.public_off", "firewall.profile.public")));
        Assert.Equal(Ids.Report("scan-1"), Ids.Report("scan-1"));
        Assert.Matches("^scan-[0-9a-f]{16}$", Ids.NewScan());
    }
}
