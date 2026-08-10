using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

/// <summary>
/// WebSocket 中转端点（/ws?token=xxx）：
/// 插件连接作为状态源（推送 state_push/event_notify），
/// 手表连接作为订阅方（接收状态/事件、发送 command），
/// command 及其回执由本中心双向转发。
/// </summary>
public static class WebSocketHub
{
    private const int ReceiveBufferSize = 64 * 1024;

    public static async Task HandleAsync(
        HttpContext context,
        ITokenService tokens,
        PeerRegistry registry,
        IStateStore store,
        ILogger logger)
    {
        var token = context.Request.Query[Protocol.QueryToken].ToString();
        if (string.IsNullOrEmpty(token) || !tokens.TryValidate(token, out var role))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        registry.Register(socket, role, out var connectionId);
        logger.LogInformation("WebSocket connected: {Role} ({Id})", role, connectionId);

        // 新手表连接时立即推送最近一次快照，保证打开即见当前状态。
        if (role == PeerRole.Watch && store.GetLatestSnapshot() is { } snapshot)
        {
            await TrySendAsync(socket, Envelope.StatePush(snapshot));
        }

        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options);
                if (envelope is null)
                {
                    continue;
                }

                envelope.Sender = role;
                await DispatchAsync(envelope, role, registry, store, logger);
            }
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning("WebSocket error ({Role}/{Id}): {Message}", role, connectionId, ex.Message);
        }
        finally
        {
            await registry.Unregister(connectionId);
            logger.LogInformation("WebSocket disconnected: {Role} ({Id})", role, connectionId);
        }
    }

    private static async Task DispatchAsync(
        Envelope envelope, PeerRole role, PeerRegistry registry, IStateStore store, ILogger logger)
    {
        switch (envelope.Type)
        {
            case Protocol.MessageTypeStatePush when role == PeerRole.Plugin:
            {
                var snapshot = JsonSerializer.Deserialize<ClassStateSnapshot>(
                    JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options);
                if (snapshot is null)
                {
                    return;
                }

                store.SaveSnapshot(snapshot);
                await registry.SendToWatchesAsync(envelope);
                logger.LogInformation("State pushed to {Count} watch(es)", registry.WatchCount);
                break;
            }

            case Protocol.MessageTypeEventNotify when role == PeerRole.Plugin:
            {
                var @event = JsonSerializer.Deserialize<ClassEvent>(
                    JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options);
                if (@event is null)
                {
                    return;
                }

                store.SaveEvent(@event);
                await registry.SendToWatchesAsync(envelope);
                break;
            }

            case Protocol.MessageTypeCommand when role == PeerRole.Watch:
                await ForwardCommandToPluginAsync(envelope, registry, logger);
                break;

            // 插件执行后的 command 回执（带 result），广播回所有手表。
            case Protocol.MessageTypeCommand when role == PeerRole.Plugin:
                await registry.SendToWatchesAsync(envelope);
                break;

            default:
                logger.LogWarning("Unhandled message type '{Type}' from {Role}", envelope.Type, role);
                break;
        }
    }

    private static async Task ForwardCommandToPluginAsync(
        Envelope envelope, PeerRegistry registry, ILogger logger)
    {
        if (await registry.SendToPluginAsync(envelope))
        {
            return;
        }

        // 无插件在线：构造失败回执发回手表。
        envelope.Payload = new CommandMessage
        {
            Command = CommandKind.SwitchWeek,
            Result = new CommandResult
            {
                Success = false,
                Message = "插件未在线，指令未执行",
            },
        };
        await registry.SendToWatchesAsync(envelope);
        logger.LogWarning("Command dropped: no plugin online");
    }

    private static async Task TrySendAsync(WebSocket socket, object envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
