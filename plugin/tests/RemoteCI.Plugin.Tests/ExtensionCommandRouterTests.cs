using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ExtensionCommandRouterTests
{
    private readonly RemoteCiExtensionRegistry _registry = new();
    private readonly ExtensionCommandRouter _router;

    public ExtensionCommandRouterTests()
    {
        _router = new ExtensionCommandRouter(_registry, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task RunExtension_InvokesRegisteredExtensionWithArgsAndIdentity()
    {
        IReadOnlyDictionary<string, string?>? receivedArgs = null;
        UserProfile? receivedUser = null;
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.say",
            "喊话",
            permission: UserPermissions.SendNotifications,
            execute: (context, args) =>
            {
                receivedArgs = args;
                receivedUser = context.RequestedBy;
                return Task.FromResult(new CommandResult
                {
                    Success = true,
                    Code = CommandResultCodes.Ok,
                    Message = "已喊话",
                });
            }));

        var user = User(userPermissions: UserPermissions.SendNotifications);
        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.say",
            ExtensionArgs = new Dictionary<string, string?> { ["text"] = "你好" },
            RequestedBy = user,
        });

        Assert.True(result.Success);
        Assert.Equal("已喊话", result.Message);
        Assert.Equal("你好", receivedArgs!["text"]);
        Assert.Equal(user.Id, receivedUser!.Id);
    }

    [Fact]
    public async Task RunExtension_RejectsUnknownExtension()
    {
        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "missing.ext",
            RequestedBy = Admin(),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public async Task RunExtension_RejectsInsufficientPermission()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.power",
            "关机",
            permission: UserPermissions.SystemControl));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.power",
            RequestedBy = User(userPermissions: UserPermissions.SendNotifications),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task RunExtension_RejectsMissingOrBlankExtensionId()
    {
        var missing = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            RequestedBy = Admin(),
        });
        Assert.Equal(CommandResultCodes.InvalidRequest, missing.Code);

        var blank = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = " ",
            RequestedBy = Admin(),
        });
        Assert.Equal(CommandResultCodes.InvalidRequest, blank.Code);
    }

    [Fact]
    public async Task RunExtension_RequiresAuthenticatedRequester()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension("demo.a", "扩展 A"));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.a",
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task RunExtension_RejectsMissingRequiredParameter()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.required",
            "必填参数",
            parameters: [new ExtensionParameter { Key = "text", Label = "内容", Required = true }]));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.required",
            RequestedBy = Admin(),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
        Assert.Contains("内容", result.Message);
    }

    [Fact]
    public async Task RunExtension_ConvertsExtensionExceptionToInternalError()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.broken",
            "会崩的扩展",
            execute: (_, _) => throw new InvalidOperationException("boom")));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.broken",
            RequestedBy = Admin(),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InternalError, result.Code);
    }

    private static UserProfile Admin() => User(userPermissions: UserPermissions.All);

    private static UserProfile User(UserPermissions userPermissions) => new()
    {
        Id = Guid.NewGuid(),
        Username = "tester",
        DisplayName = "测试用户",
        Permissions = userPermissions,
    };
}
