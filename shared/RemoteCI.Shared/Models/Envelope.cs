using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// WebSocket 消息统一信封。所有消息都走此结构，type 决定 payload 的实际形状。
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = Protocol.Version;

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("replyToMessageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplyToMessageId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 发送方角色。服务端中转时保留，便于对端识别来源。
    /// </summary>
    [JsonPropertyName("sender")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PeerRole? Sender { get; set; }

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }

    public static Envelope StatePush(object payload) =>
        New(Protocol.MessageTypeStatePush, payload);

    public static Envelope ScheduleSync(object payload) =>
        New(Protocol.MessageTypeScheduleSync, payload);

    /// <summary>请求插件立即重新生成并推送七日课表；该消息不携带可变参数。</summary>
    public static Envelope SchedulePull() => new()
    {
        Type = Protocol.MessageTypeSchedulePull,
        Payload = null,
    };

    public static Envelope EventNotify(object payload) =>
        New(Protocol.MessageTypeEventNotify, payload);

    public static Envelope ExtensionsSync(object payload) =>
        New(Protocol.MessageTypeExtensionsSync, payload);

    public static Envelope Command(object payload) =>
        New(Protocol.MessageTypeCommand, payload);

    public static Envelope CommandResult(object payload) =>
        New(Protocol.MessageTypeCommandResult, payload);

    public static Envelope AuthChallenge(object payload) =>
        New(Protocol.MessageTypeAuthChallenge, payload);

    public static Envelope AuthProof(object payload) =>
        New(Protocol.MessageTypeAuthProof, payload);

    public static Envelope AuthState(object payload) =>
        New(Protocol.MessageTypeAuthState, payload);

    public static Envelope AccountSync(object payload) =>
        New(Protocol.MessageTypeAccountSync, payload);

    public static Envelope SettingsSync(object payload) =>
        New(Protocol.MessageTypeSettingsSync, payload);

    private static Envelope New(string type, object payload) => new()
    {
        Type = type,
        Payload = payload,
    };
}
