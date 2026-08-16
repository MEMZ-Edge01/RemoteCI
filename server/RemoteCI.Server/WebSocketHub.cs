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
        ScheduleSyncService scheduleSync,
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

        // ping/pong 保活参数在 Program.cs 的 UseWebSockets 全局配置：对端无响应超过
        // KeepAliveTimeout 时由底层中止，避免 NAT/代理静默丢弃后僵尸连接永不释放。
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = registry.Register(socket, token, principal);
        logger.LogInformation("WebSocket connected: {Role}/{User} ({Id})",
            principal.PeerRole, principal.User?.Username ?? "plugin", connectionId);

        if (principal.IsPlugin)
        {
            // 本版本按“单教室单插件”设计：命令与拉取请求只投递给最早接入的插件，授权镜像仍广播给全部插件。
            if (registry.PluginCount > 1)
                logger.LogWarning("检测到 {Count} 个插件同时在线，命令将只投递给最早接入的插件，请确认是否为预期部署", registry.PluginCount);
            await registry.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(context.RequestAborted), context.RequestAborted);
            // 插件自身的启动推送可能早于云端连接完成；认证后主动拉取可消除这段竞态窗口。
            // 补齐拉取必须定向发给新接入的插件自己，不能走“最早接入优先”的单插件投递。
            await scheduleSync.StartFromPluginAsync(
                connectionId, ScheduleSyncSource.Connection, context.RequestAborted);
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
                if (scheduleSync.Current is { } task)
                    await registry.SendToWatchAsync(connectionId, Envelope.ScheduleSyncStatus(task), context.RequestAborted);
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
            var commandRate = new CommandRateLimiter();
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
                        ErrorCode = ApiErrorCodes.ProtocolVersionUnsupported,
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
                    envelope, principal, connectionId, registry, store, scheduleSync, identities, logger, commandRate, context.RequestAborted);
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
            if (principal?.IsPlugin == true)
                await scheduleSync.FailActiveAsync("插件连接已断开，课表任务未完成");
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
        ScheduleSyncService scheduleSync,
        IdentityCoordinator identities,
        ILogger logger,
        CommandRateLimiter commandRate,
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
                    // 每次插件回传都整体替换旧缓存，手动拉取不会与服务端旧课表做合并。
                    store.SaveSchedule(schedule);
                    await registry.SendScheduleToWatchesAsync(schedule, ct);
                    await scheduleSync.CompleteFromScheduleAsync(ct);
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

            case Protocol.MessageTypeScheduleSyncStatus when principal.IsPlugin:
                if (ConvertPayload<ScheduleSyncStatus>(envelope.Payload) is { } syncStatus)
                    await scheduleSync.ObserveFromPluginAsync(syncStatus, ct);
                return;

            case Protocol.MessageTypeCommandResult when principal.IsPlugin:
                if (ConvertPayload<CommandResult>(envelope.Payload) is { } result)
                    await registry.CompleteCommandAsync(envelope, result, ct);
                return;

            case Protocol.MessageTypeCommand when principal.User is not null:
                await ForwardUserCommandAsync(envelope, principal.User, connectionId, registry, identities, store, commandRate, logger, ct);
                return;

            case Protocol.MessageTypeSchedulePull when principal.User is not null:
                var pullRequest = ConvertPayload<ScheduleSyncRequest>(envelope.Payload);
                await scheduleSync.StartAsync(
                    ScheduleSyncSource.Watch, ct, pullRequest?.TaskId ?? envelope.MessageId);
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
        IStateStore store,
        CommandRateLimiter commandRate,
        ILogger logger,
        CancellationToken ct)
    {
        // 回执依赖 ReplyToMessageId 定位挂起项：缺 Id 的命令无法回执，直接丢弃并记日志。
        if (string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            logger.LogWarning("忽略缺少 messageId 的命令 ({ConnectionId})", connectionId);
            return;
        }
        if (!commandRate.TryAcquire())
        {
            await SendFailureAsync(envelope, connectionId, registry, CommandResultCodes.TooManyRequests, "命令发送过于频繁", ct);
            return;
        }

        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        if (command is null)
        {
            await SendFailureAsync(envelope, connectionId, registry, CommandResultCodes.InvalidRequest, "命令格式无效", ct);
            return;
        }

        // 通知署名是否强制由服务端全局设置决定，客户端携带的值一律被覆盖，防止绕过。
        if (command.Notification is not null)
            command.Notification.ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(ct);

        if (command.Command == CommandKind.RunExtension)
        {
            // 服务端用扩展注册表预检 RequiredPermission，防止客户端绕过手表 UI 直连云端越权；
            // 注册表尚未同步（插件未推送过）时放行，由插件执行端复核。
            if (string.IsNullOrEmpty(command.ExtensionId))
            {
                await SendFailureAsync(envelope, connectionId, registry, CommandResultCodes.InvalidRequest, "缺少扩展 Id", ct);
                return;
            }
            var definition = store.GetLatestExtensions()?.FirstOrDefault(x => x.Id == command.ExtensionId);
            if (definition is not null && !user.Permissions.HasFlag(definition.RequiredPermission))
            {
                await SendFailureAsync(envelope, connectionId, registry, CommandResultCodes.Forbidden, "权限不足", ct);
                return;
            }
        }
        else
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

    /// <summary>
    /// 单连接命令滑动窗口限速：防止单个手表连接洪泛命令把插件打爆。
    /// 限速器由每个连接的接收循环独享，无需跨连接共享状态。
    /// </summary>
    private sealed class CommandRateLimiter
    {
        private const int MaxCommandsPerWindow = 20;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private readonly Queue<long> _timestamps = new();

        public bool TryAcquire()
        {
            var now = Environment.TickCount64;
            while (_timestamps.Count > 0 && now - _timestamps.Peek() > Window.TotalMilliseconds)
                _timestamps.Dequeue();
            if (_timestamps.Count >= MaxCommandsPerWindow) return false;
            _timestamps.Enqueue(now);
            return true;
        }
    }
}
