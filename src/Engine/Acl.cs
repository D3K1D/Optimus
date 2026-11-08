using System.Security.AccessControl;
using System.Security.Principal;

namespace OptimizationTool.Engine;

/// <summary>Per-user file permissions for the data directory: current user and SYSTEM only, inheritance off.</summary>
public static class Acl
{
    public static void RestrictDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in Allowed())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void RestrictFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Allowed())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>SIDs granted access: the current user and LocalSystem.</summary>
    public static IReadOnlyList<SecurityIdentifier> Allowed()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return [identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)];
    }
}
