using RemoteCI.Shared;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class CommandPermissionsTests
{
    [Theory]
    [InlineData(CommandKind.SendNotification, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.ClearNotifications, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.TeacherComing, UserPermissions.TeacherComing)]
    [InlineData(CommandKind.SetMainMenuVisibility, UserPermissions.SystemControl)]
    [InlineData(CommandKind.Power, UserPermissions.SystemControl)]
    [InlineData(CommandKind.Volume, UserPermissions.SystemControl)]
    public void ControlCommands_UseExpectedPermission(CommandKind command, UserPermissions expected)
    {
        Assert.Equal(expected, CommandPermissions.Required(command));
    }

    [Fact]
    public void RunExtension_UsesDynamicPermissionDeclaration()
    {
        // 扩展命令的权限随注册项动态声明，静态权限表返回 None 表示不走静态校验。
        Assert.Equal(UserPermissions.None, CommandPermissions.Required(CommandKind.RunExtension));
    }
}
