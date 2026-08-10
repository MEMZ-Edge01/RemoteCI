using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>
/// WebSocket 中转集成测试：验证 插件→手表 状态推送、手表→插件 指令转发、
/// 插件→手表 指令回执三条链路。
/// </summary>
public sealed class WebSocketRelayTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public WebSocketRelayTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PluginPushesState_WatchReceivesIt()
    {
        var plugin = await ConnectAsync("plugin");
        var watch = await ConnectAsync("watch");

        var snapshot = new ClassStateSnapshot
        {
            CurrentSubject = "语文",
            NextClassSubject = "数学",
            CurrentState = ClassStateKind.Class,
            ClassPlanName = "高一(1)班课表",
            IsClassPlanLoaded = true,
            IsClassPlanEnabled = true,
        };

        await SendAsync(plugin, Envelope.StatePush(snapshot));

        var received = await ReceiveAsync<ClassStateSnapshot>(watch, Protocol.MessageTypeStatePush);
        Assert.Equal("语文", received.CurrentSubject);
        Assert.Equal("数学", received.NextClassSubject);
    }

    [Fact]
    public async Task WatchSendsCommand_PluginReceivesIt()
    {
        var plugin = await ConnectAsync("plugin");
        var watch = await ConnectAsync("watch");

        var command = new CommandMessage
        {
            Command = CommandKind.SwitchWeek,
            Parameters = new() { ["targetWeek"] = 2 },
        };
        await SendAsync(watch, Envelope.Command(command));

        var received = await ReceiveAsync<CommandMessage>(plugin, Protocol.MessageTypeCommand);
        Assert.Equal(CommandKind.SwitchWeek, received.Command);
        Assert.Equal(2, ((JsonElement)received.Parameters["targetWeek"]).GetInt32());
    }

    [Fact]
    public async Task PluginCommandResult_WatchReceivesAck()
    {
        var plugin = await ConnectAsync("plugin");
        var watch = await ConnectAsync("watch");

        // 手表发指令
        var command = new CommandMessage
        {
            Command = CommandKind.TempSwapClass,
            Parameters = new() { ["from"] = "第1节", ["to"] = "第3节" },
        };
        await SendAsync(watch, Envelope.Command(command));

        // 插件收到后回执成功
        var received = await ReceiveAsync<CommandMessage>(plugin, Protocol.MessageTypeCommand);
        received.Result = new CommandResult { Success = true, Message = "已换课" };
        await SendAsync(plugin, Envelope.Command(received));

        var ack = await ReceiveAsync<CommandMessage>(watch, Protocol.MessageTypeCommand);
        Assert.True(ack.Result?.Success);
    }

    private async Task<WebSocket> ConnectAsync(string role)
    {
        // 通过 REST 配对拿 token
        var pairResponse = await _factory.CreateClient().PostAsJsonAsync("/api/pair",
            new PairRequest { PairCode = TestWebApplicationFactory.TestPairCode, Role = role });
        pairResponse.EnsureSuccessStatusCode();
        var pair = (await pairResponse.Content.ReadFromJsonAsync<PairResponse>())!;

        var client = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(_factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={pair.Token}");
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, Envelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<T> ReceiveAsync<T>(WebSocket socket, string expectedType)
    {
        while (true)
        {
            var buffer = new byte[64 * 1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options)!;
            if (envelope.Type != expectedType)
            {
                continue; // 跳过新手表连接时补发的 state_push 等消息
            }

            return JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
        }
    }
}
