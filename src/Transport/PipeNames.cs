using System.Diagnostics;
using System.Security.Principal;

namespace OptimizationTool.Transport;

/// <summary>
/// One backend per Windows user per interactive logon session. The name is not a secret; the pipe ACL is
/// the boundary (current user only, see contracts/protocol-v1.md).
/// </summary>
public static class PipeNames
{
    public const string Prefix = "OptimizationTool.v1";

    public static string ForCurrentSession()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("No user SID for the current identity.");
        return $"{Prefix}.{sid}.{Process.GetCurrentProcess().SessionId}";
    }
}
