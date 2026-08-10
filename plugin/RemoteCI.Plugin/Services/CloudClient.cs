using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 云端客户端：连接 RemoteCI 服务端（WebSocket），推送状态/事件，
/// 接收经服务端转发的控制指令并回执。断线后每 5 秒自动重连。
/// </summary>
public sealed class CloudClient : IDisposable
{
    private const int ReconnectDelayMs = 5000;

    private readonly PluginSettings _settings;
    private readonly CommandHandler _commandHandler;
    private readonly ILogger<CloudClient> _logger;
    private readonly HttpClient _http = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public CloudClient(
        PluginSettings settings,
        CommandHandler commandHandler,
        ILogger<CloudClient> logger)
    {
        _settings = settings;
        _commandHandler = commandHandler;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken stoppingToken = default)
    {
        if (!_settings.EnableCloud)
        {
            return;
        }

        _running = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _ = RunLoopAsync(_cts.Token);
    }

    public async Task SendStateAsync(ClassStateSnapshot snapshot) =>
        await SendAsync(Envelope.StatePush(snapshot));

    public async Task SendEventAsync(ClassEvent @event) =>
        await SendAsync(Envelope.EventNotify(@event));

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                await EnsureTokenAsync(ct);
                await ConnectAsync(ct);
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "云端连接异常，{Delay}ms 后重连", ReconnectDelayMs);
                DisposeSocket();
                await Task.Delay(ReconnectDelayMs, ct);
            }
        }
    }

    /// <summary>用配对码换取云端 token（本地缓存，服务端重启失效时重新配对）。</summary>
    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_settings.CloudToken))
        {
            return;
        }

        var response = await _http.PostAsJsonAsync(
            $"{_settings.CloudServerUrl}/api/pair",
            new PairRequest { PairCode = _settings.PairCode, Role = "plugin" },
            ct);
        response.EnsureSuccessStatusCode();
        var pair = await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken: ct);
        _settings.CloudToken = pair?.Token;
        _logger.LogInformation("云端配对成功");
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        DisposeSocket();
        _ws = new ClientWebSocket();
        var uri = new UriBuilder(_settings.CloudServerUrl) { Path = "/ws", Query = $"token={_settings.CloudToken}" }.Uri;
        await _ws.ConnectAsync(uri, ct);
        _logger.LogInformation("已连接云端服务端：{Uri}", uri);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (_running && _ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, result.Count);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "server close", ct);
                    return;
                }
            }
            while (!result.EndOfMessage && result.Count > 0);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            await HandleMessageAsync(json, ct);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options);
        if (envelope?.Type != Protocol.MessageTypeCommand)
        {
            return;
        }

        var command = JsonSerializer.Deserialize<CommandMessage>(
            JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options);
        if (command is null)
        {
            return;
        }

        command.Result = _commandHandler.Handle(command);
        await SendAsync(Envelope.Command(command), ct);
    }

    private async Task SendAsync(Envelope envelope, CancellationToken ct = default)
    {
        if (_ws is not { State: WebSocketState.Open })
        {
            return; // 未连接时丢弃，等待重连循环
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private void DisposeSocket()
    {
        try
        {
            _ws?.Dispose();
        }
        catch
        {
            // 忽略关闭异常
        }

        _ws = null;
    }

    public void Dispose()
    {
        _running = false;
        _cts?.Cancel();
        DisposeSocket();
        _http.Dispose();
        _cts?.Dispose();
    }
}
