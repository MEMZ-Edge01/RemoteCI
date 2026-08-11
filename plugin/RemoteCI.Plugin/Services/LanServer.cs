using System.Collections.Concurrent;
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
    private readonly Func<ClassStateSnapshot?> _snapshotProvider;
    private readonly Func<ScheduleBundle?> _scheduleProvider;
    private readonly ILogger<LanServer> _logger;
    private readonly ConcurrentDictionary<Guid, LanClient> _clients = new();
    private WebSocketServer? _server;

    public LanServer(
        PluginSettings settings,
        AccountMirror accounts,
        CommandHandler commands,
        Func<ClassStateSnapshot?> snapshotProvider,
        Func<ScheduleBundle?> scheduleProvider,
        ILogger<LanServer> logger)
    {
        _settings = settings;
        _accounts = accounts;
        _commands = commands;
        _snapshotProvider = snapshotProvider;
        _scheduleProvider = scheduleProvider;
        _logger = logger;
    }

    public void Start()
    {
        var uri = $"ws://0.0.0.0:{_settings.LanServerPort}";
        _server = new WebSocketServer(uri) { RestartAfterListenError = true };
        _server.Start(socket =>
        {
            socket.OnOpen = () => OnOpened(socket);
            socket.OnMessage = message => _ = OnMessageAsync(socket, message);
            socket.OnClose = () => _clients.TryRemove(socket.ConnectionInfo.Id, out _);
        });
        _accounts.Changed += RefreshAuthorizations;
        _logger.LogInformation("局域网 v2 服务已启动：{Uri}/ws（授权镜像版本 {Version}）", uri, _accounts.Version);
    }

    public void BroadcastState(ClassStateSnapshot value) => Broadcast(Envelope.StatePush(value));
    public void BroadcastSchedule(ScheduleBundle value) => Broadcast(Envelope.ScheduleSync(value));
    public void BroadcastEvent(ClassEvent value) => Broadcast(Envelope.EventNotify(value));

    private void Broadcast(Envelope envelope)
    {
        foreach (var client in AuthenticatedClients()) Send(client.Socket, envelope);
    }

    private void OnOpened(IWebSocketConnection socket)
    {
        if (!string.Equals(socket.ConnectionInfo.Path.TrimEnd('/'), "/ws", StringComparison.OrdinalIgnoreCase))
        {
            socket.Close();
            return;
        }
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
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(message, JsonDefaults.Options); }
        catch (JsonException) { return; }
        if (envelope is null) return;
        if (envelope.ProtocolVersion != Protocol.Version)
        {
            Send(socket, Envelope.AuthState(new AuthState
            {
                Authenticated = false,
                ErrorCode = "PROTOCOL_VERSION_UNSUPPORTED",
                Error = $"需要协议 v{Protocol.Version}",
            }));
            socket.Close();
            return;
        }

        if (client.User is null)
        {
            await AuthenticateAsync(client, envelope);
            return;
        }

        if (envelope.Type != Protocol.MessageTypeCommand) return;
        var command = ConvertPayload<CommandMessage>(envelope.Payload);
        if (command is null) return;
        var required = CommandPermissions.Required(command.Command);
        CommandResult result;
        if (!client.User.Permissions.HasFlag(required))
        {
            result = new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.Forbidden,
                Message = _accounts.AllowsPrivilegedOperations ? "权限不足" : "授权镜像超过 24 小时，仅允许查看课程",
            };
        }
        else
        {
            command.RequestedBy = client.User;
            result = await _commands.HandleAsync(command);
        }

        Send(socket, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = envelope.MessageId,
            Payload = result,
        });
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
        Send(client.Socket, Envelope.AuthState(new AuthState { Authenticated = true, User = user }));
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
            Send(client.Socket, Envelope.AuthState(new AuthState { Authenticated = true, User = refreshed }));
        }
    }

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
        _server?.Dispose();
        _server = null;
        _clients.Clear();
    }

    private sealed class LanClient(IWebSocketConnection socket)
    {
        public IWebSocketConnection Socket { get; } = socket;
        public AuthChallenge? Challenge { get; set; }
        public UserProfile? User { get; set; }
        public Guid SessionId { get; set; }
    }
}
