using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace AiCliFeishu.Bridge.Adapters.Storage;

internal static class BridgeStoreFileSecurity
{
    public static void HardenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        HardenWindowsDirectory(path);
    }

    [SupportedOSPlatform("windows")]
    private static void HardenWindowsDirectory(string path)
    {
        var currentUser = CurrentUser();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        AddDirectoryRule(security, currentUser);
        AddDirectoryRule(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddDirectoryRule(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void HardenFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        HardenWindowsFile(path);
    }

    [SupportedOSPlatform("windows")]
    private static void HardenWindowsFile(string path)
    {
        var currentUser = CurrentUser();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        AddFileRule(security, currentUser);
        AddFileRule(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFileRule(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException(
            "无法识别当前 Windows 用户，拒绝创建未受保护的 Bridge Store。 ");
    }

    [SupportedOSPlatform("windows")]
    private static void AddDirectoryRule(
        DirectorySecurity security,
        SecurityIdentifier identity) => security.AddAccessRule(
            new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

    [SupportedOSPlatform("windows")]
    private static void AddFileRule(
        FileSecurity security,
        SecurityIdentifier identity) => security.AddAccessRule(
            new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
}
