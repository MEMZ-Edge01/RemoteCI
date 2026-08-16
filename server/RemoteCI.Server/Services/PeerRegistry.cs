using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>连接注册表，同时负责命令的定向回执与权限变更后的在线连接刷新。</summary>
public sealed class PeerRegistry(IServiceScopeFactory scopeFactory, ILogger<PeerRegistry> logger)
{
    private static readonly TimeSpan WatchCommandTimeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, WsPeer> _pluginPeers = new();
    private readonly ConcurrentDictionary<Guid, WsPeer> _watchPeers = new();
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
    private PluginNetworkInfo? _latestPluginNetworkInfo;

    public bool HasPlugin => !_pluginPeers.IsEmpty;
    public int WatchCount => _watchPeers.Count;
    public int PluginCount => _pluginPeers.Count;

    public Guid Register(WebSocket socket, string token, AuthPrincipal principal)
    {
        var id = Guid.NewGuid();
        TableFor(principal.PeerRole)[id] = new WsPeer(id, token, principal, socket);
        return id;
    }

    public async Task UnregisterAsync(Guid connectionId, WebSocketCloseStatus? status = null)
    {
        var peer = _pluginPeers.TryRemove(connectionId, out var plugin) ? plugin
            : _watchPeers.TryRemove(connectionId, out var watch) ? watch
            : null;
        if (peer is null) return;

        foreach (var pending in _pendingCommands.Where(x => x.Value.WatchConnectionId == connectionId).ToList())
        {
            if (_pendingCommands.TryRemove(pending.Key, out var removed))
                removed.Completion?.TrySetResult(CommandResult.Failure(CommandResultCodes.Unauthorized, "连接已断开"));
        }

        try
        {
            if (peer.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // 对端无响应时 CloseAsync 会永远等待关闭握手；5 秒超时后强制中止。
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await peer.Socket.CloseAsync(
                    status ?? WebSocketCloseStatus.NormalClosure, "closed", closeCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            peer.Socket.Abort();
        }
        catch (WebSocketException)
        {
            // 对端已经断开，无需二次处理。
        }
        catch (ObjectDisposedException)
        {
            // 对端 socket 已释放（测试宿主或连接中断场景），同样无需二次处理。
        }
        catch (IOException)
        {
            // 连接中断（含测试宿主把已释放连接包装为 IO 异常的情况），由调用方继续清理。
        }
        finally
        {
            peer.SendLock.Dispose();
        }
    }

    public Task SendSnapshotToWatchesAsync(ClassStateSnapshot value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.StatePush(value), ct);

    public Task SendScheduleToWatchesAsync(ScheduleBundle value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.ScheduleSync(value), ct);

    public Task SendScheduleSyncStatusToWatchesAsync(ScheduleSyncStatus value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.ScheduleSyncStatus(value), ct);

    public Task SendEventToWatchesAsync(ClassEvent value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.EventNotify(value), ct);

    public Task SendExtensionsToWatchesAsync(IReadOnlyList<ExtensionDefinition> value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.ExtensionsSync(value), ct);

    public Task SendSettingsToWatchesAsync(SettingsSync value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.SettingsSync(value), ct);

    /// <summary>缓存插件最近一次网卡发现结果，并同步给所有在线手表。</summary>
    public Task PublishPluginNetworkInfoAsync(PluginNetworkInfo value, CancellationToken ct = default)
    {
        Volatile.Write(ref _latestPluginNetworkInfo, value);
        return BroadcastWatchesAsync(Envelope.PluginNetworkInfo(value), ct);
    }

    public Task SendLatestPluginNetworkInfoToWatchAsync(Guid connectionId, CancellationToken ct = default) =>
        Volatile.Read(ref _latestPluginNetworkInfo) is { } value
            ? SendToWatchAsync(connectionId, Envelope.PluginNetworkInfo(value), ct)
            : Task.CompletedTask;

    public async Task BroadcastWatchesAsync(Envelope envelope, CancellationToken ct = default)
    {
        foreach (var peer in _watchPeers.Values)
        {
            if (await RefreshAsync(peer, ct) is null || !await TrySendAsync(peer, envelope, ct))
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    /// <summary>广播给全部插件（幂等内容：授权镜像同步）。失效连接顺带清理。</summary>
    public async Task<bool> BroadcastToPluginsAsync(Envelope envelope, CancellationToken ct = default)
    {
        var sent = false;
        foreach (var peer in _pluginPeers.Values)
        {
            if (await RefreshAsync(peer, ct) is not null && await TrySendAsync(peer, envelope, ct))
            {
                sent = true;
                continue;
            }

            // 发送失败说明注册表中的连接已经失效，清理后由插件自身的重连循环重新接入。
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
        return sent;
    }

    /// <summary>
    /// 命令与只读请求只投递给一个插件：多插件在线时选择最早接入的健康插件，
    /// 避免同一命令被多个 ClassIsland 实例重复执行（换课/关机/通知各执行一次以上）。
    /// </summary>
    public async Task<bool> SendToPluginAsync(Envelope envelope, CancellationToken ct = default)
    {
        // 按注册时间排序才是真正的“最早接入优先”；Guid 顺序与接入时间无关。
        foreach (var peer in _pluginPeers.Values.OrderBy(x => x.RegisteredAt))
        {
            if (await RefreshAsync(peer, ct) is not null && await TrySendAsync(peer, envelope, ct))
                return true;
            await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
        return false;
    }

    public Task<bool> SendAccountSyncToPluginsAsync(AccountSync sync, CancellationToken ct = default) =>
        BroadcastToPluginsAsync(Envelope.AccountSync(sync), ct);

    /// <summary>请求最早接入的在线插件立即重新生成课表；返回是否成功发送。</summary>
    public Task<bool> RequestSchedulePullAsync(ScheduleSyncRequest request, CancellationToken ct = default) =>
        SendToPluginAsync(Envelope.SchedulePull(request), ct);

    /// <summary>
    /// 向指定插件连接发送课表拉取请求：新插件接入时的补齐拉取必须定向发给它自己，
    /// 否则多插件在线时拉取会被投递给最早的插件，新插件的启动竞态窗口无法消除。
    /// </summary>
    public async Task<bool> RequestSchedulePullFromAsync(
        Guid connectionId, ScheduleSyncRequest request, CancellationToken ct = default)
    {
        if (_pluginPeers.TryGetValue(connectionId, out var peer) && await RefreshAsync(peer, ct) is not null)
            return await TrySendAsync(peer, Envelope.SchedulePull(request), ct);
        return false;
    }

    public async Task SendToWatchAsync(Guid connectionId, Envelope envelope, CancellationToken ct = default)
    {
        if (_watchPeers.TryGetValue(connectionId, out var peer) && await RefreshAsync(peer, ct) is not null)
            await TrySendAsync(peer, envelope, ct);
    }

    public void RegisterWatchCommand(string messageId, Guid connectionId)
    {
        _pendingCommands[messageId] = new PendingCommand(connectionId, null);
        _ = ExpireWatchCommandAsync(messageId, connectionId);
    }

    /// <summary>与 REST 路径的 15 秒上限一致：插件超时未回执就回收挂起项并告知发起手表，避免永久泄漏。</summary>
    private async Task ExpireWatchCommandAsync(string messageId, Guid connectionId)
    {
        await Task.Delay(WatchCommandTimeout);
        if (!_pendingCommands.TryRemove(messageId, out var pending)) return;
        if (pending.WatchConnectionId != connectionId || pending.Completion is not null) return;
        await SendToWatchAsync(connectionId, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = messageId,
            Payload = CommandResult.Failure(CommandResultCodes.Timeout, "等待插件回执超时，操作结果未知"),
        });
    }

    public async Task<CommandResult> SendCommandAndWaitAsync(
        CommandMessage command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!HasPlugin) return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        var envelope = Envelope.Command(command);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[envelope.MessageId] = new PendingCommand(null, completion);
        if (!await SendToPluginAsync(envelope, ct))
        {
            _pendingCommands.TryRemove(envelope.MessageId, out _);
            return CommandResult.Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            _pendingCommands.TryRemove(envelope.MessageId, out _);
            return CommandResult.Failure(CommandResultCodes.Timeout, "等待插件回执超时，操作结果未知");
        }
    }

    public async Task<bool> CompleteCommandAsync(Envelope envelope, CommandResult result, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envelope.ReplyToMessageId) ||
            !_pendingCommands.TryRemove(envelope.ReplyToMessageId, out var pending))
            return false;

        pending.Completion?.TrySetResult(result);
        if (pending.WatchConnectionId is not { } connectionId) return true;
        await SendToWatchAsync(connectionId, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = envelope.ReplyToMessageId,
            Payload = result,
        }, ct);
        return true;
    }

    public async Task RefreshWatchAuthorizationsAsync(CancellationToken ct = default)
    {
        foreach (var peer in _watchPeers.Values)
        {
            var principal = await RefreshAsync(peer, ct);
            if (principal?.User is null)
            {
                await TrySendAsync(peer, Envelope.AuthState(new AuthState
                {
                    Authenticated = false,
                    ErrorCode = ApiErrorCodes.Unauthorized,
                    Error = "账号或设备会话已失效",
                }), ct);
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
                continue;
            }
            await TrySendAsync(
                peer,
                Envelope.AuthState(ServerAuthStateFactory.CreateAuthenticated(principal.User)),
                ct);
        }
    }

    private async Task<AuthPrincipal?> RefreshAsync(WsPeer peer, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
            var principal = await identities.ValidateAnyTokenAsync(peer.Token, ct);
            if (principal is not null) peer.Principal = principal;
            return principal;
        }
        catch (SqliteException ex) when (!ct.IsCancellationRequested)
        {
            // SQLite 瞬时锁/IO 错误不应击穿健康的 WS 连接：沿用上次验证通过的身份，
            // 权限若真有变更，后续 RefreshWatchAuthorizationsAsync 会补上踢下线。
            logger.LogWarning("令牌校验瞬时失败，沿用上次身份 ({Id}): {Message}", peer.Id, ex.Message);
            return peer.Principal;
        }
    }

    private ConcurrentDictionary<Guid, WsPeer> TableFor(PeerRole role) =>
        role == PeerRole.Plugin ? _pluginPeers : _watchPeers;

    private static async Task<bool> TrySendAsync(WsPeer peer, Envelope envelope, CancellationToken ct)
    {
        try
        {
            if (peer.Socket.State != WebSocketState.Open) return false;
            await peer.SendLock.WaitAsync(ct);
            try
            {
                if (peer.Socket.State != WebSocketState.Open) return false;
                var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
                if (peer.Socket.State != WebSocketState.Open) return false;
                await peer.Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
                return true;
            }
            finally
            {
                peer.SendLock.Release();
            }
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            // 对端已释放连接，等待 UnregisterAsync 清理注册表，本次发送直接跳过。
            return false;
        }
        catch (IOException)
        {
            // 连接中断（含测试宿主把已释放连接包装为 IO 异常的情况），由 UnregisterAsync 清理。
            return false;
        }
    }

    private sealed record PendingCommand(Guid? WatchConnectionId, TaskCompletionSource<CommandResult>? Completion);

    private sealed class WsPeer(Guid id, string token, AuthPrincipal principal, WebSocket socket)
    {
        public Guid Id { get; } = id;
        public string Token { get; } = token;
        public AuthPrincipal Principal { get; set; } = principal;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        /// <summary>接入时刻（UtcNow Ticks），用于“最早接入优先”的投递排序。</summary>
        public long RegisteredAt { get; } = DateTime.UtcNow.Ticks;
    }
}
