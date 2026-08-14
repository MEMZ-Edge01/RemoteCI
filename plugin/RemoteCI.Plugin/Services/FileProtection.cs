using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 敏感文件防护：在 Windows 上移除 “Users” 组的读取规则并显式授予当前用户修改权，
/// 防止同机其他账号读取插件凭据（Settings.json）与设备验证器（Accounts.json）。
/// 非 Windows 平台或任何失败都静默降级——功能可用性优先于纵深防御。
/// </summary>
internal static class FileProtection
{
    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        RestrictCore(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictCore(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            var builtinUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            foreach (var rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                         .OfType<FileSystemAccessRule>()
                         .Where(rule => rule.IdentityReference == builtinUsers)
                         .ToList())
            {
                security.RemoveAccessRule(rule);
            }

            var current = WindowsIdentity.GetCurrent().User;
            if (current is not null)
                security.AddAccessRule(new FileSystemAccessRule(
                    current, FileSystemRights.Modify, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            // ACL 收紧失败不影响插件功能，仅少一层纵深防御。
        }
    }
}
