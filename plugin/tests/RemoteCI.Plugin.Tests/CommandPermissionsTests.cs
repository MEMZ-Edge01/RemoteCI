using RemoteCI.Shared;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class CommandPermissionsTests
{
    [Theory]
    [InlineData(CommandKind.SendNotification, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.ClearNotifications, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.TeacherComing, UserPermissions.TeacherComing)]
    [InlineData(CommandKind.SetMainMenuVisibility, UserPermissions.MainMenuControl)]
    [InlineData(CommandKind.Power, UserPermissions.PowerControl)]
    [InlineData(CommandKind.Volume, UserPermissions.PowerControl)]
    public void ControlCommands_UseExpectedPermission(CommandKind command, UserPermissions expected)
    {
        Assert.Equal(expected, CommandPermissions.Required(command));
    }

    [Fact]
    public void RunExtension_UsesIndependentExtensionAuthorization()
    {
        // 扩展命令由 RunExtensions 和逐扩展策略统一授权，不走普通命令的静态权限表。
        Assert.Equal(UserPermissions.None, CommandPermissions.Required(CommandKind.RunExtension));
    }

    [Fact]
    public void BreakingPermissionModelUsesProtocolVersionThree()
    {
        Assert.Equal(3, Protocol.Version);
        Assert.Equal("REMOTECI_DISCOVER_V3", Protocol.LanDiscoveryRequest);
    }
}
