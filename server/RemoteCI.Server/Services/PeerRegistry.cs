using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// WebSocket 连接注册表：维护插件连接（状态源）与手表连接（订阅方），
/// 负责消息转发。连接按角色隔离，插件至多一个（单班级），手表可多个。
/// </summary>
public sealed class PeerRegistry
{
    private readonly ConcurrentDictionary<Guid, WsPeer> _pluginPeers = new();
    private readonly ConcurrentDictionary<Guid, WsPeer> _watchPeers = new();
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    /// <summary>注册一个连接。</summary>
    public void Register(WebSocket socket, PeerRole role, out Guid connectionId)
    {
        connectionId = Guid.NewGuid();
        var peer = new WsPeer(connectionId, role, socket);
        var table = TableFor(role);
        table[connectionId] = peer;
    }

    /// <summary>移除一个连接并关闭底层 socket。</summary>
    public async Task Unregister(Guid connectionId, WebSocketCloseStatus? status = null)
    {
        var peer = _pluginPeers.TryRemove(connectionId, out var p1) ? p1
                 : _watchPeers.TryRemove(connectionId, out var p2) ? p2
                 : null;
        if (peer is null)
        {
            return;
        }

        try
        {
            if (peer.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await peer.Socket.CloseAsync(
                    status ?? WebSocketCloseStatus.NormalClosure,
                    "closed", CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // 对端可能已断开，忽略。
        }
    }

    /// <summary>是否有插件在线。</summary>
    public bool HasPlugin => !_pluginPeers.IsEmpty;

    /// <summary>当前在线手表连接数。</summary>
    public int WatchCount => _watchPeers.Count;

    /// <summary>把消息广播给所有手表连接。</summary>
    public async Task SendToWatchesAsync(Envelope envelope, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
        await BroadcastAsync(_watchPeers, payload, ct);
    }

    /// <summary>把消息发送给插件连接。</summary>
    public async Task<bool> SendToPluginAsync(Envelope envelope, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
        var sent = false;
        foreach (var (_, peer) in _pluginPeers)
        {
            sent |= await TrySendAsync(peer, payload, ct);
        }
        return sent;
    }

    private ConcurrentDictionary<Guid, WsPeer> TableFor(PeerRole role) =>
        role == PeerRole.Plugin ? _pluginPeers : _watchPeers;

    private static async Task BroadcastAsync(
        ConcurrentDictionary<Guid, WsPeer> peers, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        foreach (var (id, peer) in peers)
        {
            var ok = await TrySendAsync(peer, payload, ct);
            if (!ok)
            {
                peers.TryRemove(id, out _);
            }
        }
    }

    private static async Task<bool> TrySendAsync(
        WsPeer peer, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (peer.Socket.State != WebSocketState.Open)
        {
            return false;
        }

        try
        {
            await peer.Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
    }

    /// <summary>一个已注册的连接。</summary>
    public sealed record WsPeer(Guid Id, PeerRole Role, WebSocket Socket);
}
