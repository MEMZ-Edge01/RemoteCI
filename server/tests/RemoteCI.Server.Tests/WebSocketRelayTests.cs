using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class WebSocketRelayTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public WebSocketRelayTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task WatchAuthentication_ReportsConnectedServerVersion()
    {
        using var watch = await ConnectWatchAsync();

        var auth = await ReceivePayloadAsync<AuthState>(watch, Protocol.MessageTypeAuthState);

        Assert.True(auth.Authenticated);
        Assert.Equal(AppVersion.Version, auth.ServerVersion);
    }

    [Fact]
    public async Task PluginConnection_ImmediatelyRequestsFreshSchedule()
    {
        using var plugin = await ConnectPluginAsync();

        var request = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);

        Assert.Equal(Protocol.Version, request.ProtocolVersion);
    }

    [Fact]
    public async Task AuthenticatedWatchSchedulePull_IsForwardedWithoutScheduleManagementPermission()
    {
        using var plugin = await ConnectPluginAsync();
        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
        var admin = await _factory.LoginAsync();
        var create = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "schedule.reader",
                DisplayName = "课表查看者",
                Password = "Schedule-Reader-Password-2026",
            }));
        create.EnsureSuccessStatusCode();
        using var watch = await ConnectWatchAsync("schedule.reader", "Schedule-Reader-Password-2026");

        await SendAsync(watch, Envelope.SchedulePull());

        await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeSchedulePull);
    }

    [Fact]
    public async Task PluginPushesCurrentStateAndSevenDaySchedule_AllWatchesReceiveBoth()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        await SendAsync(plugin, Envelope.StatePush(new ClassStateSnapshot
        {
            CurrentSubject = "语文",
            CurrentState = ClassStateKind.Class,
            IsClassPlanLoaded = true,
        }));
        await SendAsync(plugin, Envelope.ScheduleSync(new ScheduleBundle
        {
            FromDate = "2026-08-11",
            Days = [new ScheduleDay
            {
                Date = "2026-08-11",
                Revision = "revision-a",
                Enabled = true,
                Courses = [new CourseEntry { Index = 0, Label = "第 1 节", SubjectId = Guid.NewGuid(), Subject = "语文" }],
            }],
        }));

        var state = await ReceivePayloadAsync<ClassStateSnapshot>(watch, Protocol.MessageTypeStatePush);
        var schedule = await ReceivePayloadAsync<ScheduleBundle>(watch, Protocol.MessageTypeScheduleSync);
        Assert.Equal("语文", state.CurrentSubject);
        Assert.Equal("revision-a", schedule.Days.Single().Revision);
        Assert.Single(schedule.Days.Single().Courses);
    }

    [Fact]
    public async Task PluginNetworkInfo_IsRelayedAndCachedForWatches()
    {
        using var plugin = await ConnectPluginAsync();
        using var connectedWatch = await ConnectWatchAsync();
        var expected = new PluginNetworkInfo
        {
            LanServerEnabled = true,
            Port = 9876,
            Addresses = ["192.168.50.8", "10.0.0.8"],
        };

        await SendAsync(plugin, Envelope.PluginNetworkInfo(expected));

        var relayed = await ReceivePayloadAsync<PluginNetworkInfo>(
            connectedWatch, Protocol.MessageTypePluginNetworkInfo);
        Assert.Equal(expected.Port, relayed.Port);
        Assert.Equal(expected.Addresses, relayed.Addresses);

        using var laterWatch = await ConnectWatchAsync();
        var cached = await ReceivePayloadAsync<PluginNetworkInfo>(
            laterWatch, Protocol.MessageTypePluginNetworkInfo);
        Assert.Equal(expected.Port, cached.Port);
        Assert.Equal(expected.Addresses, cached.Addresses);
    }

    [Fact]
    public async Task AdminCommand_IsForwardedAndResultReturnsOnlyByCorrelationId()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.ChangeSchedule,
            ScheduleChange = new ScheduleChangeRequest
            {
                Date = "2026-08-11",
                Mode = ScheduleChangeMode.Exchange,
                SourceIndex = 0,
                TargetIndex = 1,
                ExpectedRevision = "revision-a",
            },
        });
        await SendAsync(watch, request);

        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.ChangeSchedule, command.Command);
        Assert.Equal(UserRole.Admin, command.RequestedBy?.Role);

        var result = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已换课" };
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = result,
        });

        var reply = await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeCommandResult);
        var received = ConvertPayload<CommandResult>(reply.Payload);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
        Assert.True(received.Success);
        Assert.Equal("已换课", received.Message);
    }

    [Fact]
    public async Task OrdinaryUserCommand_IsRejectedBeforePluginExecution()
    {
        var admin = await _factory.LoginAsync();
        var create = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "ws.student",
                DisplayName = "WebSocket 学生",
                Password = "Student-Password-2026",
            }));
        create.EnsureSuccessStatusCode();

        using var watch = await ConnectWatchAsync("ws.student", "Student-Password-2026");
        await SendAsync(watch, Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest { Title = "x", Message = "x" },
        }));
        var result = await ReceivePayloadAsync<CommandResult>(watch, Protocol.MessageTypeCommandResult);
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task PermissionChanges_PushVersionedPasswordFreeMirrorToPlugin()
    {
        using var plugin = await ConnectPluginAsync();
        var initialSync = await ReceivePayloadAsync<AccountSync>(plugin, Protocol.MessageTypeAccountSync);
        Assert.Equal(AppVersion.Version, initialSync.ServerVersion);
        var admin = await _factory.LoginAsync();
        var create = await _factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "sync.student",
                DisplayName = "同步学生",
                Password = "Sync-Student-Password-2026",
                GrantedPermissions = UserPermissions.SendNotifications,
            }));
        create.EnsureSuccessStatusCode();

        // 登录管理员也会创建新设备会话并推送一个镜像，因此按账号版本等待创建用户后的镜像。
        AccountSync sync;
        do
        {
            sync = await ReceivePayloadAsync<AccountSync>(plugin, Protocol.MessageTypeAccountSync);
        }
        while (sync.Version <= initialSync.Version || sync.Accounts.All(x => x.Username != "sync.student"));
        var account = Assert.Single(sync.Accounts, x => x.Username == "sync.student");
        Assert.Equal(UserPermissions.ViewCurrentCourse | UserPermissions.SendNotifications, account.EffectivePermissions);
        var json = JsonSerializer.Serialize(sync, JsonDefaults.Options);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(sync.Version > 0);
    }

    [Fact]
    public async Task PluginPushesExtensions_AllWatchesReceiveThem()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        await SendAsync(plugin, Envelope.ExtensionsSync(new List<ExtensionDefinition>
        {
            new ExtensionDefinition
            {
                Id = "demo.lock",
                DisplayName = "锁屏",
                Icon = "power",
                RequiredPermission = UserPermissions.SystemControl,
            },
        }));

        var received = await ReceivePayloadAsync<List<ExtensionDefinition>>(watch, Protocol.MessageTypeExtensionsSync);
        var extension = Assert.Single(received);
        Assert.Equal("demo.lock", extension.Id);
        Assert.Equal("锁屏", extension.DisplayName);
        Assert.Equal(UserPermissions.SystemControl, extension.RequiredPermission);
    }

    [Fact]
    public async Task NewWatchConnection_ReceivesCachedExtensions()
    {
        using var plugin = await ConnectPluginAsync();
        await SendAsync(plugin, Envelope.ExtensionsSync(new List<ExtensionDefinition>
        {
            new ExtensionDefinition
            {
                Id = "demo.lock",
                DisplayName = "锁屏",
                RequiredPermission = UserPermissions.SystemControl,
            },
        }));

        using var watch = await ConnectWatchAsync();
        var received = await ReceivePayloadAsync<List<ExtensionDefinition>>(watch, Protocol.MessageTypeExtensionsSync);
        Assert.Equal("demo.lock", Assert.Single(received).Id);
    }

    [Fact]
    public async Task ExtensionCommand_IsForwardedToPluginAndResultReturns()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();
        var request = Envelope.Command(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.lock",
            ExtensionArgs = new Dictionary<string, string?> { ["message"] = "下课了" },
        });
        await SendAsync(watch, request);

        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.RunExtension, command.Command);
        Assert.Equal("demo.lock", command.ExtensionId);
        Assert.Equal("下课了", command.ExtensionArgs!["message"]);
        Assert.Equal(UserRole.Admin, command.RequestedBy?.Role);

        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已执行" },
        });

        var reply = await ReceiveEnvelopeAsync(watch, Protocol.MessageTypeCommandResult);
        Assert.Equal(request.MessageId, reply.ReplyToMessageId);
    }

    [Fact]
    public async Task WatchReceivesDefaultSettings_AndNotificationCommandGetsServerPolicyInjected()
    {
        using var plugin = await ConnectPluginAsync();
        using var watch = await ConnectWatchAsync();

        var settings = await ReceivePayloadAsync<SettingsSync>(watch, Protocol.MessageTypeSettingsSync);
        Assert.True(settings.ForceSenderInTitle);

        await SendAsync(watch, Envelope.Command(new CommandMessage
        {
            Command = CommandKind.SendNotification,
            Notification = new NotificationRequest { Title = "x", Message = "x" },
        }));
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.True(command.Notification?.ForceSenderInTitle);
    }

    private async Task<WebSocket> ConnectPluginAsync() => await ConnectAsync(await _factory.GetPluginTokenAsync());

    private async Task<WebSocket> ConnectWatchAsync(
        string username = TestWebApplicationFactory.AdminUsername,
        string password = TestWebApplicationFactory.AdminPassword) =>
        await ConnectAsync((await _factory.LoginAsync(username, password)).AccessToken);

    private async Task<WebSocket> ConnectAsync(string token)
    {
        var client = _factory.Server.CreateWebSocketClient();
        return await client.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"),
            CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, Envelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<T> ReceivePayloadAsync<T>(WebSocket socket, string expectedType) =>
        ConvertPayload<T>((await ReceiveEnvelopeAsync(socket, expectedType)).Payload);

    private static async Task<Envelope> ReceiveEnvelopeAsync(WebSocket socket, string expectedType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var buffer = new byte[256 * 1024];
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options)!;
            if (envelope.Type == expectedType) return envelope;
        }
    }

    private static T ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options)!;
}
