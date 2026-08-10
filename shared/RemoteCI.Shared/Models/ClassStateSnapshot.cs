using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 课表状态快照：插件定时/事件触发时生成，推送给手表（state_push）。
/// 字段均为可空/带默认值，保证协议向后兼容。
/// </summary>
public sealed class ClassStateSnapshot
{
    /// <summary>当前所处时间点的科目；未加载课表为 null。</summary>
    [JsonPropertyName("currentSubject")]
    public string? CurrentSubject { get; set; }

    /// <summary>下一节课（下一个上课类型时间点）的科目。</summary>
    [JsonPropertyName("nextClassSubject")]
    public string? NextClassSubject { get; set; }

    /// <summary>当前时间状态。</summary>
    [JsonPropertyName("currentState")]
    public ClassStateKind CurrentState { get; set; } = ClassStateKind.None;

    /// <summary>当前时间点描述（如 "08:00-08:45 语文"）。</summary>
    [JsonPropertyName("currentTimeLayoutItem")]
    public string? CurrentTimeLayoutItem { get; set; }

    /// <summary>下一个上课时间点描述。</summary>
    [JsonPropertyName("nextClassTimeLayoutItem")]
    public string? NextClassTimeLayoutItem { get; set; }

    /// <summary>当前课表名称。</summary>
    [JsonPropertyName("classPlanName")]
    public string? ClassPlanName { get; set; }

    /// <summary>当前周次序号（多周轮换，从 1 开始）；未启用轮换时为 null。</summary>
    [JsonPropertyName("weekRotation")]
    public int? WeekRotation { get; set; }

    /// <summary>是否启用课表。</summary>
    [JsonPropertyName("isClassPlanEnabled")]
    public bool IsClassPlanEnabled { get; set; }

    /// <summary>是否已加载课表。</summary>
    [JsonPropertyName("isClassPlanLoaded")]
    public bool IsClassPlanLoaded { get; set; }

    /// <summary>上课类型时间点剩余时间；仅上课状态有意义。</summary>
    [JsonPropertyName("onClassLeftTime")]
    public TimeSpan? OnClassLeftTime { get; set; }

    /// <summary>课间类型时间点剩余时间；仅课间状态有意义。</summary>
    [JsonPropertyName("onBreakingLeftTime")]
    public TimeSpan? OnBreakingLeftTime { get; set; }

    /// <summary>是否已确定当前时间点。</summary>
    [JsonPropertyName("lessonConfirmed")]
    public bool LessonConfirmed { get; set; }

    /// <summary>快照生成时间（UTC）。</summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
