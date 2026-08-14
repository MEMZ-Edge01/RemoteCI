using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

public static class WebSocketHub
{
    private const int ReceiveBufferSize = 256 * 1024;

    public static async Task HandleAsync(
        HttpContext context,
        IdentityCoordinator identities,
        PeerRegistry registry,
        IStateStore store,
        ILogger logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = context.Request.Query[Protocol.QueryToken].ToString();
        var principal = await identities.ValidateAnyTokenAsync(token, context.RequestAborted);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = registry.Register(socket, token, principal);
        logger.LogInformation("WebSocket connected: {Role}/{User} ({Id})",
            principal.PeerRole, principal.User?.Username ?? "plugin", connectionId);

        if (principal.IsPlugin)
        {
            // 本版本按“单教室单插件”设计：命令会广播给所有在线插件并以首个回执为准。
            if (registry.PluginCount > 1)
                logger.LogWarning("检测到 {Count} 个插件同时在线，命令将广播给全部插件并以首个回执为准，请确认是否为预期部署", registry.PluginCount);
            await registry.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(context.RequestAborted), context.RequestAborted);
            // 插件自身的启动推送可能早于云端连接完成；认证后主动拉取可消除这段竞态窗口。
            await registry.RequestSchedulePullAsync(context.RequestAborted);
        }
        else
        {
            try
            {
                await registry.SendToWatchAsync(
                    connectionId,
                    Envelope.AuthState(ServerAuthStateFactory.CreateAuthenticated(principal.User)),
                    context.RequestAborted);
                await registry.SendLatestPluginNetworkInfoToWatchAsync(connectionId, context.RequestAborted);
                if (store.GetLatestSnapshot() is { } snapshot)
                    await registry.SendToWatchAsync(connectionId, Envelope.StatePush(snapshot), context.RequestAborted);
                if (store.GetLatestSchedule() is { } schedule)
                    await registry.SendToWatchAsync(connectionId, Envelope.ScheduleSync(schedule), context.RequestAborted);
                if (store.GetLatestExtensions() is { } extensions)
                    await registry.SendToWatchAsync(connectionId, Envelope.ExtensionsSync(extensions), context.RequestAborted);
                await registry.SendToWatchAsync(connectionId, Envelope.SettingsSync(new SettingsSync
                {
                    ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(context.RequestAborted),
                }), context.RequestAborted);
            }
            catch (Exception ex)
            {
                // 初始化推送失败时记录原因并关闭连接，避免半初始化状态悬挂。
                logger.LogError(ex, "WebSocket 初始化推送失败 ({Id})", connectionId);
                await registry.UnregisterAsync(connectionId, WebSocketCloseStatus.InternalServerError);
                return;
            }
        }

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(socket, context.RequestAborted);
                if (json is null) break;
                Envelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options)!;
                }
                catch (JsonException)
                {
                    // 单条畸形消息不应终止整个健康会话。
                    logger.LogWarning("忽略无法解析的 WebSocket 消息 ({Id})", connectionId);
                    continue;
                }
                if (envelope.ProtocolVersion != Protocol.Version)
                {
                    var versionError = Envelope.AuthState(new AuthState
                    {
                        Authenticated = false,
                        ErrorCode = "PROTOCOL_VERSION_UNSUPPORTED",
                        Error = $"需要协议 v{Protocol.Version}，当前为 v{envelope.ProtocolVersion}",
                    });
                    if (principal.PeerRole == PeerRole.Plugin)
                    {
                        // 插件连接不在手表注册表中，直接写当前 socket。
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(versionError, JsonDefaults.Options);
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, context.RequestAborted);
                    }
                    else
                    {
                        await registry.SendToWatchAsync(connectionId, versionError, context.RequestAborted);
                    }
                    break;
                }

                principal = await identities.ValidateAnyTokenAsync(token, context.RequestAborted);
                if (principal is null) break;
                envelope.Sender = principal.PeerRole;
                await DispatchAsync(
                    envelope, principal, connectionId, registry, store, identities, logger, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("WebSocket 请求被取消: {Id}", connectionId);
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning("WebSocket error ({Id}): {Message}", connectionId, ex.Message);
        }
        finally
        {
            await registry.UnregisterAsync(connectionId);
            logger.LogInformation("WebSocket disconnected: {Id}", connectionId);
        }
    }

    private static async Task DispatchAsync(
        Envelope envelope,
        AuthPrincipal principal,
        Guid connectionId,
        PeerRegistry registry,
        IStateStore store,
        IdentityCoordinator identities,
        ILogger logger,
        CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case Protocol.MessageTypeStatePush when principal.IsPlugin:
                if (ConvertPayload<ClassStateSnapshot>(envelope.Payload) is { } snapshot)
                {
                    store.SaveSnapshot(snapshot);
                    await registry.SendSnapshotToWatchesAsync(snapshot, ct);
                }
                return;

            case Protocol.MessageTypeScheduleSync when principal.IsPlugin:
                if (ConvertPayload<ScheduleBundle>(envelope.Payload) is { } schedule)
                {
                    store.SaveSchedule(schedule);
                    await registry.SendScheduleToWatchesAsync(schedule, ct);
                }
                return;

            case Protocol.MessageTypeEventNotify when principal.IsPlugin:
                if (ConvertPayload<ClassEvent>(envelope.Payload) is { } value)
                {
                    store.SaveEvent(value);
                    await registry.SendEventToWatchesAsync(value, ct);
                }
                return;

            case Protocol.MessageTypeExtensionsSync when principal.IsPlugin:
                if (ConvertPayload<List<ExtensionDefinition>>(envelope.Payload) is { } extensions)
                {
                    store.SaveExtensions(extensions);
                    await registry.SendExtensionsToWatchesAsync(extensions, ct);
                }
                return;

            case Protocol.MessageTypePluginNetworkInfo when principal.IsPlugin:
                if (NormalizePluginNetworkInfo(ConvertPayload<PluginNetworkInfo>(envelope.Payload)) is { } networkInfo)
                    await registry.PublishPluginNetworkInfoAsync(networkInfo, ct);
                else
                    logger.LogWarning("插件上报了无效的局域网地址或端口");
                return;

            case Protocol.MessageTypeCommandResult when principal.IsPlugin:
                if (ConvertPayload<CommandResult>(envelope.Payload) is { } result)
                    await registry.CompleteCommandAsync(envelope, result, ct);
                return;

            case Protocol.MessageTypeCommand when principal.User is not null:
                await ForwardUserCommandAsync(envelope, principal.User, connectionId, registry, identities, ct);
                return;

            case Protocol.MessageTypeSchedulePull when principal.User is not null:
                await registry.RequestSchedulePullAsync(ct);
                return;

            default:
                logger.LogWarning("Unhandled message type {Type} from {Role}", envelope.Type, principal.PeerRole);
                return;
        }
    }

    private static async Task ForwardUserCommandAsync(
        Envelope envelope,
        UserProfile user,
        Guid connectionId,
        PeerRegistry registry,
        IdentityCoordinator identities,
        CancellationToken ct)
    {
        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        if (command is null)
        {
            await SendFailureAsync(envelope, connectionId, registry, CommandResultCodes.InvalidRequest, "命令格式无效", ct);
            return;
        }

        // 通知署名是否强制由服务端全局设置决定，客户端携带的值一律被覆盖，防止绕过。
        if (command.Notification is not null)
            command.Notification.ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(ct);

        // RunExtension 的所需权限由插件端按注册项动态校验，服务端只要求已认证用户。
        if (command.Command != CommandKind.RunExtension)
        {
            var required = CommandPermissions.Required(command.Command);
            if (required == UserPermissions.None)
            {
                await SendFailureAsync(envelope, connectionId, registry, CommandResultCodes.InvalidRequest, "未知命令", ct);
                return;
            }
            if (!user.Permissions.HasFlag(required))
            {
                await SendFailureAsync(envelope, connectionId, registry, CommandResultCodes.Forbidden, "权限不足", ct);
                return;
            }
        }

        command.RequestedBy = user;
        envelope.Payload = command;
        registry.RegisterWatchCommand(envelope.MessageId, connectionId);
        if (!await registry.SendToPluginAsync(envelope, ct))
            await registry.CompleteCommandAsync(new Envelope
            {
                Type = Protocol.MessageTypeCommandResult,
                ReplyToMessageId = envelope.MessageId,
                Payload = new CommandResult(),
            }, new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.PluginOffline,
                Message = "插件未在线，操作未执行",
            }, ct);
    }

    private static Task SendFailureAsync(
        Envelope request, Guid connectionId, PeerRegistry registry, string code, string message, CancellationToken ct) =>
        registry.SendToWatchAsync(connectionId, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = request.MessageId,
            Payload = new CommandResult { Success = false, Code = code, Message = message },
        }, ct);

    private static T? ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);

    private static PluginNetworkInfo? NormalizePluginNetworkInfo(PluginNetworkInfo? value)
    {
        if (value is null || value.Port is < 1 or > 65535) return null;
        var addresses = value.Addresses
            .Select(address => address?.Trim())
            .Where(address => !string.IsNullOrEmpty(address) && System.Net.IPAddress.TryParse(address, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Cast<string>()
            .ToArray();
        if (value.LanServerEnabled && addresses.Length == 0) return null;
        return new PluginNetworkInfo
        {
            LanServerEnabled = value.LanServerEnabled,
            Addresses = addresses,
            Port = value.Port,
        };
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType == WebSocketMessageType.Binary)
                throw new WebSocketException("不支持二进制帧");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > ReceiveBufferSize) throw new WebSocketException("消息过大");
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
