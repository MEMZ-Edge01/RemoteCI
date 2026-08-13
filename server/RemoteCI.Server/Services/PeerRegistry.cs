using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>连接注册表，同时负责命令的定向回执与权限变更后的在线连接刷新。</summary>
public sealed class PeerRegistry(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<Guid, WsPeer> _pluginPeers = new();
    private readonly ConcurrentDictionary<Guid, WsPeer> _watchPeers = new();
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new(StringComparer.Ordinal);

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
                removed.Completion?.TrySetResult(Failure(CommandResultCodes.Unauthorized, "连接已断开"));
        }

        try
        {
            if (peer.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await peer.Socket.CloseAsync(status ?? WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
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

    public Task SendEventToWatchesAsync(ClassEvent value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.EventNotify(value), ct);

    public Task SendExtensionsToWatchesAsync(IReadOnlyList<ExtensionDefinition> value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.ExtensionsSync(value), ct);

    public Task SendSettingsToWatchesAsync(SettingsSync value, CancellationToken ct = default) =>
        BroadcastWatchesAsync(Envelope.SettingsSync(value), ct);

    public async Task BroadcastWatchesAsync(Envelope envelope, CancellationToken ct = default)
    {
        foreach (var peer in _watchPeers.Values)
        {
            if (await RefreshAsync(peer, ct) is null || !await TrySendAsync(peer, envelope, ct))
                await UnregisterAsync(peer.Id, WebSocketCloseStatus.PolicyViolation);
        }
    }

    public async Task<bool> SendToPluginAsync(Envelope envelope, CancellationToken ct = default)
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

    public Task<bool> SendAccountSyncToPluginsAsync(AccountSync sync, CancellationToken ct = default) =>
        SendToPluginAsync(Envelope.AccountSync(sync), ct);

    public async Task SendToWatchAsync(Guid connectionId, Envelope envelope, CancellationToken ct = default)
    {
        if (_watchPeers.TryGetValue(connectionId, out var peer) && await RefreshAsync(peer, ct) is not null)
            await TrySendAsync(peer, envelope, ct);
    }

    public void RegisterWatchCommand(string messageId, Guid connectionId) =>
        _pendingCommands[messageId] = new PendingCommand(connectionId, null);

    public async Task<CommandResult> SendCommandAndWaitAsync(
        CommandMessage command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!HasPlugin) return Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        var envelope = Envelope.Command(command);
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[envelope.MessageId] = new PendingCommand(null, completion);
        if (!await SendToPluginAsync(envelope, ct))
        {
            _pendingCommands.TryRemove(envelope.MessageId, out _);
            return Failure(CommandResultCodes.PluginOffline, "插件未在线，操作未执行");
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            _pendingCommands.TryRemove(envelope.MessageId, out _);
            return Failure(CommandResultCodes.Timeout, "等待插件回执超时，操作结果未知");
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
            await TrySendAsync(peer, Envelope.AuthState(new AuthState { Authenticated = true, User = principal.User }), ct);
        }
    }

    private async Task<AuthPrincipal?> RefreshAsync(WsPeer peer, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var principal = await identities.ValidateAnyTokenAsync(peer.Token, ct);
        if (principal is not null) peer.Principal = principal;
        return principal;
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

    private static CommandResult Failure(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };

    private sealed record PendingCommand(Guid? WatchConnectionId, TaskCompletionSource<CommandResult>? Completion);

    private sealed class WsPeer(Guid id, string token, AuthPrincipal principal, WebSocket socket)
    {
        public Guid Id { get; } = id;
        public string Token { get; } = token;
        public AuthPrincipal Principal { get; set; } = principal;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}
