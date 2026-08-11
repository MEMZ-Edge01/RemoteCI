using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>插件云端通道：长期插件凭据、版本化授权同步和真实命令回执。</summary>
public sealed class CloudClient : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly PluginSettings _settings;
    private readonly AccountMirror _accounts;
    private readonly CommandHandler _commands;
    private readonly ILogger<CloudClient> _logger;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public CloudClient(
        PluginSettings settings,
        AccountMirror accounts,
        CommandHandler commands,
        ILogger<CloudClient> logger)
    {
        _settings = settings;
        _accounts = accounts;
        _commands = commands;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken stoppingToken = default)
    {
        if (!_settings.EnableCloud) return Task.CompletedTask;
        _running = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _ = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task SendStateAsync(ClassStateSnapshot value) => SendAsync(Envelope.StatePush(value));
    public Task SendScheduleAsync(ScheduleBundle value) => SendAsync(Envelope.ScheduleSync(value));
    public Task SendEventAsync(ClassEvent value) => SendAsync(Envelope.EventNotify(value));

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
                if (IsAuthenticationFailure(ex))
                {
                    _settings.CloudToken = null;
                    _logger.LogWarning("插件云端凭据已失效，请在 WebUI 生成新的一次性配对码");
                }
                else
                {
                    _logger.LogWarning(ex, "云端连接异常，{Delay} 秒后重连", ReconnectDelay.TotalSeconds);
                }
                DisposeSocket();
                await Task.Delay(ReconnectDelay, ct);
            }
        }
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.CloudToken)) return;
        if (string.IsNullOrWhiteSpace(_settings.PluginPairCode))
            throw new InvalidOperationException("尚未配置一次性插件配对码");

        var response = await _http.PostAsJsonAsync(
            $"{_settings.CloudServerUrl.TrimEnd('/')}/api/plugin/pair",
            new PairRequest { PairCode = _settings.PluginPairCode, Role = "plugin" },
            ct);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized)
            throw new PluginAuthenticationException();
        response.EnsureSuccessStatusCode();
        var pair = await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("插件配对响应为空");
        _settings.CloudToken = pair.Token;
        _settings.PluginPairCode = string.Empty;
        _logger.LogInformation("插件云端长期凭据已签发，一次性配对码已清除");
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        DisposeSocket();
        _ws = new ClientWebSocket();
        var builder = new UriBuilder(_settings.CloudServerUrl)
        {
            Path = "/ws",
            Query = $"token={Uri.EscapeDataString(_settings.CloudToken ?? string.Empty)}",
        };
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        await _ws.ConnectAsync(builder.Uri, ct);
        _logger.LogInformation("已连接 RemoteCI 云端：{Host}", builder.Uri.Host);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (_running && _ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (result.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                        throw new PluginAuthenticationException();
                    return;
                }
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 256 * 1024) throw new InvalidDataException("云端消息超过 256 KiB");
            } while (!result.EndOfMessage);

            await HandleMessageAsync(Encoding.UTF8.GetString(stream.ToArray()), ct);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options);
        if (envelope is null) return;
        if (envelope.ProtocolVersion != Protocol.Version)
            throw new InvalidDataException($"服务端协议为 v{envelope.ProtocolVersion}，插件要求 v{Protocol.Version}");

        if (envelope.Type == Protocol.MessageTypeAccountSync)
        {
            var sync = ConvertPayload<AccountSync>(envelope.Payload);
            if (sync is not null)
            {
                _accounts.Apply(sync);
                _logger.LogInformation("账号、权限和设备会话已同步到版本 {Version}", sync.Version);
            }
            return;
        }

        if (envelope.Type != Protocol.MessageTypeCommand) return;
        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        var result = command is null
            ? new CommandResult { Success = false, Code = CommandResultCodes.InvalidRequest, Message = "命令格式无效" }
            : await _commands.HandleAsync(command);
        var response = Envelope.CommandResult(result);
        response.ReplyToMessageId = envelope.MessageId;
        await SendAsync(response, ct);
    }

    private async Task SendAsync(Envelope envelope, CancellationToken ct = default)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws is { State: WebSocketState.Open })
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static T? ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);

    private static bool IsAuthenticationFailure(Exception ex) => ex is PluginAuthenticationException ||
        ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase);

    private void DisposeSocket()
    {
        try { _ws?.Dispose(); } catch { /* 忽略关闭竞争。 */ }
        _ws = null;
    }

    public void Dispose()
    {
        _running = false;
        _cts?.Cancel();
        DisposeSocket();
        _http.Dispose();
        _sendLock.Dispose();
        _cts?.Dispose();
    }

    private sealed class PluginAuthenticationException : Exception;
}
