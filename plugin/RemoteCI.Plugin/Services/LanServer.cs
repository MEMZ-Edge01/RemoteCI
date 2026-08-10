using System.Collections.Concurrent;
using Fleck;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 局域网 WebSocket 服务：手表在同一 WiFi 下直连插件（ws://电脑IP:端口/ws/配对码）。
/// 使用 Fleck 内嵌服务器，不依赖 ASP.NET Core 运行时，任何机器都能运行。
/// </summary>
public sealed class LanServer : IDisposable
{
    private readonly PluginSettings _settings;
    private readonly CommandHandler _commandHandler;
    private readonly Func<ClassStateSnapshot?> _snapshotProvider;
    private readonly ILogger<LanServer> _logger;
    private readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new();
    private WebSocketServer? _server;

    public LanServer(
        PluginSettings settings,
        CommandHandler commandHandler,
        Func<ClassStateSnapshot?> snapshotProvider,
        ILogger<LanServer> logger)
    {
        _settings = settings;
        _commandHandler = commandHandler;
        _snapshotProvider = snapshotProvider;
        _logger = logger;
    }

    public void Start()
    {
        var uri = $"ws://0.0.0.0:{_settings.LanServerPort}";
        _server = new WebSocketServer(uri)
        {
            RestartAfterListenError = true,
        };

        _server.Start(socket =>
        {
            socket.OnOpen = () => OnOpened(socket);
            socket.OnMessage = message => OnMessage(socket, message);
            socket.OnClose = () => _clients.TryRemove(socket.ConnectionInfo.Id, out _);
        });

        _logger.LogInformation("局域网服务已启动：{Uri}（配对码 {PairCode}）", uri, _settings.PairCode);
    }

    public void BroadcastState(ClassStateSnapshot snapshot) =>
        Broadcast(Envelope.StatePush(snapshot));

    public void BroadcastEvent(ClassEvent @event) =>
        Broadcast(Envelope.EventNotify(@event));

    private void OnOpened(IWebSocketConnection socket)
    {
        // 认证：连接路径必须为 /ws/{配对码}
        var expected = $"/ws/{Uri.EscapeDataString(_settings.PairCode)}";
        if (!string.Equals(socket.ConnectionInfo.Path, expected, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("拒绝未授权的局域网连接：{Path}", socket.ConnectionInfo.Path);
            socket.Close();
            return;
        }

        _clients[socket.ConnectionInfo.Id] = socket;
        _logger.LogInformation("手表已接入局域网：{Ip}", socket.ConnectionInfo.ClientIpAddress);

        // 新连接立即收到最近一次快照
        if (_snapshotProvider() is { } snapshot)
        {
            Send(socket, Envelope.StatePush(snapshot));
        }
    }

    private void OnMessage(IWebSocketConnection socket, string message)
    {
        var envelope = System.Text.Json.JsonSerializer.Deserialize<Envelope>(
            message, JsonDefaults.Options);
        if (envelope?.Type != Protocol.MessageTypeCommand)
        {
            return;
        }

        var command = System.Text.Json.JsonSerializer.Deserialize<CommandMessage>(
            System.Text.Json.JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options);
        if (command is null)
        {
            return;
        }

        command.Result = _commandHandler.Handle(command);
        Broadcast(Envelope.Command(command));
    }

    private void Broadcast(Envelope envelope)
    {
        foreach (var (id, socket) in _clients)
        {
            if (!Send(socket, envelope))
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private static bool Send(IWebSocketConnection socket, Envelope envelope)
    {
        if (socket.IsAvailable)
        {
            socket.Send(System.Text.Json.JsonSerializer.Serialize(envelope, JsonDefaults.Options));
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
        _clients.Clear();
    }
}
