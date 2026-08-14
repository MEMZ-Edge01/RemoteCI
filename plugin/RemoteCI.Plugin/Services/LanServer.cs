using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Fleck;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>局域网接入端：一次性挑战＋设备会话 HMAC，和云端使用同一有效权限。</summary>
public sealed class LanServer : IDisposable
{
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromSeconds(30);
    private readonly PluginSettings _settings;
    private readonly AccountMirror _accounts;
    private readonly CommandHandler _commands;
    private readonly SchedulePullRequestHandler _schedulePullRequests;
    private readonly Func<ClassStateSnapshot?> _snapshotProvider;
    private readonly Func<ScheduleBundle?> _scheduleProvider;
    private readonly ILogger<LanServer> _logger;
    private readonly ConcurrentDictionary<Guid, LanClient> _clients = new();
    private WebSocketServer? _server;
    private LanDiscoveryResponder? _discovery;

    public LanServer(
        PluginSettings settings,
        AccountMirror accounts,
        CommandHandler commands,
        Action requestFreshSchedule,
        Func<ClassStateSnapshot?> snapshotProvider,
        Func<ScheduleBundle?> scheduleProvider,
        ILogger<LanServer> logger)
    {
        _settings = settings;
        _accounts = accounts;
        _commands = commands;
        _schedulePullRequests = new SchedulePullRequestHandler(requestFreshSchedule);
        _snapshotProvider = snapshotProvider;
        _scheduleProvider = scheduleProvider;
        _logger = logger;
    }

    public void Start()
    {
        try
        {
            var uri = $"ws://0.0.0.0:{_settings.LanServerPort}";
            _server = new WebSocketServer(uri) { RestartAfterListenError = true };
            _server.Start(socket =>
            {
                socket.OnOpen = () => OnOpened(socket);
                socket.OnMessage = message => _ = OnMessageAsync(socket, message);
                socket.OnClose = () => _clients.TryRemove(socket.ConnectionInfo.Id, out _);
            });
        }
        catch (SocketException ex)
        {
            // 端口被占用时只停用局域网服务，不能让异常冒泡到 ClassIsland 启动流程。
            _logger.LogWarning(ex, "无法监听局域网端口 {Port}，局域网直连与设备扫描不可用", _settings.LanServerPort);
            _server?.Dispose();
            _server = null;
            return;
        }
        _discovery = new LanDiscoveryResponder(_settings, _logger);
        _discovery.Start();
        _accounts.Changed += RefreshAuthorizations;
        _logger.LogInformation("局域网 v2 服务已启动：{Uri}/ws（授权镜像版本 {Version}）", $"ws://0.0.0.0:{_settings.LanServerPort}", _accounts.Version);
    }

    public void BroadcastState(ClassStateSnapshot value) => Broadcast(Envelope.StatePush(value));
    public void BroadcastSchedule(ScheduleBundle value) => Broadcast(Envelope.ScheduleSync(value));
    public void BroadcastEvent(ClassEvent value) => Broadcast(Envelope.EventNotify(value));
    public void BroadcastExtensions(IReadOnlyList<ExtensionDefinition> value) => Broadcast(Envelope.ExtensionsSync(value));

    private void Broadcast(Envelope envelope)
    {
        foreach (var client in AuthenticatedClients())
        {
            // 发送与连接关闭之间存在竞态，单个客户端失败不能影响其余客户端，
            // 更不能让异常沿每秒执行的状态推送链冒泡到 UI 线程。
            try
            {
                Send(client.Socket, envelope);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "向局域网客户端 {Id} 广播失败", client.Socket.ConnectionInfo.Id);
            }
        }
    }

    private void OnOpened(IWebSocketConnection socket)
    {
        var path = socket.ConnectionInfo.Path.TrimEnd('/');
        if (string.Equals(path, "/bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            // 未登录设备只能读取云端连接信息；密码和会话凭据始终直接交给云服务器。
            Send(socket, LanDiscoveryProtocol.CreateBootstrapEnvelope(_settings, Environment.MachineName));
            socket.Close();
            return;
        }
        if (!string.Equals(path, "/ws", StringComparison.OrdinalIgnoreCase))
        {
            socket.Close();
            return;
        }
        CleanupExpiredChallenges();
        var now = DateTimeOffset.UtcNow;
        var challenge = new AuthChallenge
        {
            ChallengeId = Guid.NewGuid().ToString("N"),
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            ExpiresAt = now + ChallengeTtl,
        };
        _clients[socket.ConnectionInfo.Id] = new LanClient(socket) { Challenge = challenge };
        Send(socket, Envelope.AuthChallenge(challenge));
    }

    private async Task OnMessageAsync(IWebSocketConnection socket, string message)
    {
        if (!_clients.TryGetValue(socket.ConnectionInfo.Id, out var client)) return;
        // 同一条连接的消息串行处理，避免命令并发执行与回执乱序。
        await client.Gate.WaitAsync();
        try
        {
            await HandleMessageCoreAsync(client, message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理局域网客户端 {Id} 消息失败", socket.ConnectionInfo.Id);
        }
        finally
        {
            client.Gate.Release();
        }
    }

    private async Task HandleMessageCoreAsync(LanClient client, string message)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(message, JsonDefaults.Options); }
        catch (JsonException) { return; }
        if (envelope is null) return;
        if (envelope.ProtocolVersion != Protocol.Version)
        {
            Send(client.Socket, Envelope.AuthState(new AuthState
            {
                Authenticated = false,
                ErrorCode = "PROTOCOL_VERSION_UNSUPPORTED",
                Error = $"需要协议 v{Protocol.Version}",
            }));
            client.Socket.Close();
            return;
        }

        if (client.User is null)
        {
            await AuthenticateAsync(client, envelope);
            return;
        }

        if (_schedulePullRequests.TryHandle(envelope)) return;
        if (envelope.Type != Protocol.MessageTypeCommand) return;
        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        if (command is null) return;
        var required = CommandPermissions.Required(command.Command);
        // 授权镜像过期时所有管理命令都必须拒绝；扩展命令的静态权限为 None，
        // 因此需要单独检查镜像状态，避免绕过“仅允许查看课程”的限制。
        var mirrorExpired = !_accounts.AllowsPrivilegedOperations;
        var permissionDenied = !client.User.Permissions.HasFlag(required);
        CommandResult result;
        if (mirrorExpired || permissionDenied)
        {
            result = new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.Forbidden,
                Message = mirrorExpired ? "授权镜像超过 24 小时，仅允许查看课程" : "权限不足",
            };
        }
        else
        {
            command.RequestedBy = client.User;
            result = await _commands.HandleAsync(command);
        }

        Send(client.Socket, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = envelope.MessageId,
            Payload = result,
        });
    }

    /// <summary>清理挑战已过期且从未完成认证的连接，避免字典被扫描流量撑大。</summary>
    private void CleanupExpiredChallenges()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _clients)
        {
            if (pair.Value.User is null && pair.Value.Challenge is { } challenge && challenge.ExpiresAt <= now)
            {
                if (_clients.TryRemove(pair.Key, out var removed))
                    removed.Socket.Close();
            }
        }
    }

    private Task AuthenticateAsync(LanClient client, Envelope envelope)
    {
        if (envelope.Type != Protocol.MessageTypeAuthProof || client.Challenge is not { } challenge ||
            challenge.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Send(client.Socket, Envelope.AuthState(new AuthState
            {
                Authenticated = false,
                ErrorCode = ApiErrorCodes.Unauthorized,
                Error = "局域网认证挑战已失效",
            }));
            client.Socket.Close();
            return Task.CompletedTask;
        }

        client.Challenge = null; // 先消费挑战，任何失败都不能重放。
        var proof = ConvertPayload<AuthProof>(envelope.Payload);
        if (proof is null || proof.ChallengeId != challenge.ChallengeId ||
            !_accounts.TryVerify(proof.DeviceSessionId, challenge, proof.ClientNonce, proof.Proof, out var user) ||
            user is null)
        {
            Send(client.Socket, Envelope.AuthState(new AuthState
            {
                Authenticated = false,
                ErrorCode = ApiErrorCodes.Unauthorized,
                Error = "设备会话无效，请先通过 HTTPS 重新登录",
            }));
            client.Socket.Close();
            return Task.CompletedTask;
        }

        client.User = user;
        client.SessionId = proof.DeviceSessionId;
        SendAuthenticatedState(client, user);
        if (_snapshotProvider() is { } snapshot) Send(client.Socket, Envelope.StatePush(snapshot));
        if (_scheduleProvider() is { } schedule) Send(client.Socket, Envelope.ScheduleSync(schedule));
        _logger.LogInformation("局域网设备会话已认证：{Username}/{Role}", user.Username, user.Role);
        return Task.CompletedTask;
    }

    private void RefreshAuthorizations()
    {
        foreach (var client in AuthenticatedClients())
        {
            var refreshed = _accounts.GetProfileForSession(client.User!.Id, client.SessionId);
            if (refreshed is null)
            {
                Send(client.Socket, Envelope.AuthState(new AuthState
                {
                    Authenticated = false,
                    ErrorCode = ApiErrorCodes.Unauthorized,
                    Error = "账号或设备会话已失效",
                }));
                client.Socket.Close();
                continue;
            }
            client.User = refreshed;
            SendAuthenticatedState(client, refreshed);
        }
    }

    private void SendAuthenticatedState(LanClient client, UserProfile user) =>
        Send(client.Socket, Envelope.AuthState(new AuthState
        {
            Authenticated = true,
            ServerVersion = _accounts.ServerVersion,
            User = user,
        }));

    private IEnumerable<LanClient> AuthenticatedClients() =>
        _clients.Values.Where(x => x.User is not null && x.Socket.IsAvailable);

    private static T? ConvertPayload<T>(object? payload) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);

    private static void Send(IWebSocketConnection socket, Envelope envelope)
    {
        if (socket.IsAvailable) socket.Send(JsonSerializer.Serialize(envelope, JsonDefaults.Options));
    }

    public void Dispose()
    {
        _accounts.Changed -= RefreshAuthorizations;
        _discovery?.Dispose();
        _discovery = null;
        _server?.Dispose();
        _server = null;
        _clients.Clear();
    }

    private sealed class LanClient(IWebSocketConnection socket)
    {
        public IWebSocketConnection Socket { get; } = socket;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AuthChallenge? Challenge { get; set; }
        public UserProfile? User { get; set; }
        public Guid SessionId { get; set; }
    }
}
