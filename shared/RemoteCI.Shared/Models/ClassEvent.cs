using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 课程事件（event_notify 载荷），手表据此触发通知+振动。
/// </summary>
public sealed class ClassEvent
{
    [JsonPropertyName("event")]
    public ClassEventKind Event { get; set; }

    /// <summary>事件关联科目（上课/课间事件有值）。</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    /// <summary>面向用户的提示文案（插件侧生成，中文）。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>事件发生时间（UTC）。</summary>
    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
