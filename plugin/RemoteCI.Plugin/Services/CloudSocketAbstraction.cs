using System.Net.WebSockets;

namespace RemoteCI.Plugin.Services;

/// <summary>CloudClient 对 WebSocket 的最小抽象，便于测试注入假连接。</summary>
internal interface ICloudSocket : IDisposable
{
    WebSocketState State { get; }
    WebSocketCloseStatus? CloseStatus { get; }
    TimeSpan KeepAliveInterval { set; }
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(byte[] buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct);
    Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken ct);
}

/// <summary>生产环境实现：包装 ClientWebSocket，行为与直接使用完全一致。</summary>
internal sealed class ClientWebSocketAdapter(ClientWebSocket socket) : ICloudSocket
{
    public WebSocketState State => socket.State;
    public WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
    public TimeSpan KeepAliveInterval { set => socket.Options.KeepAliveInterval = value; }
    public Task ConnectAsync(Uri uri, CancellationToken ct) => socket.ConnectAsync(uri, ct);
    public Task SendAsync(byte[] buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(buffer), messageType, endOfMessage, ct);
    public Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken ct) =>
        socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
    public void Dispose() => socket.Dispose();
}
