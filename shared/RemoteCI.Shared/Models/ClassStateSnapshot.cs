using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>高频状态快照，不包含低频七日课表。</summary>
public sealed class ClassStateSnapshot
{
    [JsonPropertyName("scheduleDate")]
    public string? ScheduleDate { get; set; }

    [JsonPropertyName("currentSubject")]
    public string? CurrentSubject { get; set; }

    [JsonPropertyName("nextClassSubject")]
    public string? NextClassSubject { get; set; }

    [JsonPropertyName("currentState")]
    public ClassStateKind CurrentState { get; set; }

    [JsonPropertyName("currentTimeLayoutItem")]
    public string? CurrentTimeLayoutItem { get; set; }

    [JsonPropertyName("nextClassTimeLayoutItem")]
    public string? NextClassTimeLayoutItem { get; set; }

    [JsonPropertyName("classPlanName")]
    public string? ClassPlanName { get; set; }

    [JsonPropertyName("isClassPlanEnabled")]
    public bool IsClassPlanEnabled { get; set; }

    [JsonPropertyName("isClassPlanLoaded")]
    public bool IsClassPlanLoaded { get; set; }

    [JsonPropertyName("onClassLeftTime")]
    public TimeSpan? OnClassLeftTime { get; set; }

    [JsonPropertyName("onBreakingLeftTime")]
    public TimeSpan? OnBreakingLeftTime { get; set; }

    [JsonPropertyName("lessonConfirmed")]
    public bool LessonConfirmed { get; set; }

    [JsonPropertyName("isNotificationPlaying")]
    public bool IsNotificationPlaying { get; set; }

    [JsonPropertyName("isMainMenuVisible")]
    public bool IsMainMenuVisible { get; set; } = true;

    [JsonPropertyName("isSleepAvailable")]
    public bool IsSleepAvailable { get; set; }

    [JsonPropertyName("isHibernateAvailable")]
    public bool IsHibernateAvailable { get; set; }

    [JsonPropertyName("isVolumeControlAvailable")]
    public bool IsVolumeControlAvailable { get; set; }

    [JsonPropertyName("volumePercent")]
    public int VolumePercent { get; set; }

    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; set; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
