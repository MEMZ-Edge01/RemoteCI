using RemoteCI.Shared;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class CommandPermissionsTests
{
    [Theory]
    [InlineData(CommandKind.SendNotification, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.ClearNotifications, UserPermissions.SendNotifications)]
    [InlineData(CommandKind.SetMainMenuVisibility, UserPermissions.SystemControl)]
    [InlineData(CommandKind.Power, UserPermissions.SystemControl)]
    public void ControlCommands_UseExpectedPermission(CommandKind command, UserPermissions expected)
    {
        Assert.Equal(expected, CommandPermissions.Required(command));
    }
}
