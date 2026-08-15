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
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CredentiallessRetryDelay = TimeSpan.FromMinutes(2);
    private readonly PluginSettings _settings;
    private readonly AccountMirror _accounts;
    private readonly CloudTokenStore? _tokenStore;
    private readonly Func<CommandMessage, Task<CommandResult>> _handleCommand;
    private readonly SchedulePullRequestHandler _schedulePullRequests;
    private readonly ILogger<CloudClient> _logger;
    private readonly Func<ICloudSocket> _socketFactory;
    private readonly TimeSpan _initialReconnectDelay;
    // 配对请求设置显式超时，服务器挂起时不会让重连循环永久阻塞。
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ICloudSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private TimeSpan _reconnectDelay;

    internal CloudClient(
        PluginSettings settings,
        AccountMirror accounts,
        CommandHandler? commands,
        Action requestFreshSchedule,
        ILogger<CloudClient> logger,
        CloudTokenStore? tokenStore = null,
        Func<ICloudSocket>? socketFactory = null,
        HttpMessageHandler? httpHandler = null,
        TimeSpan? reconnectDelay = null)
    {
        _settings = settings;
        _accounts = accounts;
        _tokenStore = tokenStore;
        // 测试可注入命令执行替身；生产走 CommandHandler（唯一写操作入口）。
        _handleCommand = commands is not null
            ? commands.HandleAsync
            : _ => Task.FromResult(new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.InternalError,
                Message = "命令执行器未初始化",
            });
        _schedulePullRequests = new SchedulePullRequestHandler(requestFreshSchedule);
        _logger = logger;
        _socketFactory = socketFactory ?? (() => new ClientWebSocketAdapter(new ClientWebSocket()));
        _http = new HttpClient(httpHandler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        _initialReconnectDelay = reconnectDelay ?? ReconnectDelay;
        _reconnectDelay = _initialReconnectDelay;
    }

    /// <summary>长期凭据访问器：内存缓存 + DPAPI 落盘（凭据吊销时同步删除存储文件）。</summary>
    private string? Token
    {
        get => _settings.CloudToken;
        set
        {
            _settings.CloudToken = value;
            _tokenStore?.Save(value);
        }
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
    public Task SendExtensionsAsync(IReadOnlyList<ExtensionDefinition> value) => SendAsync(Envelope.ExtensionsSync(value));

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                await EnsureTokenAsync(ct);
                await ConnectAsync(ct);
                _reconnectDelay = _initialReconnectDelay; // 连接成功即复位退避。
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_running || ct.IsCancellationRequested) break;
                if (IsAuthenticationFailure(ex))
                {
                    Token = null;
                    _logger.LogWarning("插件云端凭据已失效，请在 WebUI 生成新的一次性配对码");
                }
                else
                {
                    _logger.LogWarning(ex, "云端连接异常，{Delay} 秒后重连", _reconnectDelay.TotalSeconds);
                }
                DisposeSocket();
                // 没有任何凭据时退避到 2 分钟，避免每 5 秒刷日志空转；循环仍会周期性拾取新填写的配对码。
                var delay = string.IsNullOrWhiteSpace(Token) &&
                    string.IsNullOrWhiteSpace(_settings.PluginPairCode)
                    ? CredentiallessRetryDelay
                    : _reconnectDelay;
                // 指数退避至上限（生产 5s→10s→…→60s；测试注入毫秒级初始值以便快速验证）。
                _reconnectDelay = TimeSpan.FromTicks(Math.Min(_reconnectDelay.Ticks * 2, MaxReconnectDelay.Ticks));
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (Exception waitEx) when (waitEx is OperationCanceledException or ObjectDisposedException)
                {
                    // 退避等待被取消（插件停止）时静默退出，不能把取消异常留给未观察任务。
                    break;
                }
            }
        }
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Token)) return;
        if (string.IsNullOrWhiteSpace(_settings.PluginPairCode))
            throw new InvalidOperationException("尚未配置一次性插件配对码");

        var response = await _http.PostAsJsonAsync(
            $"{_settings.CloudServerUrl.TrimEnd('/')}/api/plugin/pair",
            new PairRequest { PairCode = _settings.PluginPairCode, Role = "plugin" },
            ct);
        // 409=配对码已用、401=配对码无效、403=端点拒绝（反向代理或权限配置），均视为凭据失效。
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new PluginAuthenticationException();
        response.EnsureSuccessStatusCode();
        var pair = await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("插件配对响应为空");
        Token = pair.Token;
        _settings.PluginPairCode = string.Empty;
        _logger.LogInformation("插件云端长期凭据已签发，一次性配对码已清除");
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        DisposeSocket();
        _ws = _socketFactory();
        // 空闲时由协议层 ping 探测；静默断网会在 ping 超时后让接收循环报错并触发重连。
        _ws.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var builder = new UriBuilder(_settings.CloudServerUrl)
        {
            Path = "/ws",
            Query = $"token={Uri.EscapeDataString(Token ?? string.Empty)}",
        };
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        await _ws.ConnectAsync(builder.Uri, ct);
        // 每次重连都重新发现网卡，避免 DHCP 或 Wi-Fi 切换后把过期地址留给手表。
        await SendAsync(Envelope.PluginNetworkInfo(PluginNetworkInfoProvider.Create(_settings)), ct);
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

        if (_schedulePullRequests.TryHandle(envelope)) return;

        if (envelope.Type != Protocol.MessageTypeCommand) return;
        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        var result = command is null
            ? new CommandResult { Success = false, Code = CommandResultCodes.InvalidRequest, Message = "命令格式无效" }
            : await _handleCommand(command);
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
            if (_ws is { State: WebSocketState.Open } socket)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (WebSocketException)
        {
            // 发送失败说明对端已不可达：立即释放连接，让接收循环解除阻塞并触发外层重连。
            DisposeSocket();
        }
        catch (ObjectDisposedException)
        {
            // 与 DisposeSocket/Dispose 的正常竞态，无需处理。
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { /* 插件正在停止。 */ }
        }
    }

    private static T? ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);

    /// <summary>凭据失效只由明确的类型信号判定：配对端点 401/403/409 与 WS 的 PolicyViolation。
    /// 不再用异常消息字符串匹配，避免错误文本或 URL 恰好含 “401” 时误清长期凭据。</summary>
    private static bool IsAuthenticationFailure(Exception ex) => ex is PluginAuthenticationException;

    private void DisposeSocket()
    {
        try { _ws?.Dispose(); } catch { /* 忽略关闭竞争。 */ }
        _ws = null;
    }

    public void Dispose()
    {
        // 先置位 _running 再取消：RunLoop 各异常分支会检查这两个信号并静默退出，
        // 因此退避等待与发送路径已能容忍 CTS 释放，不再泄漏取消源句柄。
        // _sendLock 仍可能被在途发送持有，留待 GC 终结器回收。
        _running = false;
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();
        DisposeSocket();
        _http.Dispose();
    }

    private sealed class PluginAuthenticationException : Exception;
}
