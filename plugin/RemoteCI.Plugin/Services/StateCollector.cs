using System.Diagnostics;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 课表状态收集器：从 ClassIsland 的 ILessonsService 读取课表状态并生成协议快照。
/// 订阅上课/下课/放学/状态改变事件产生通知，主计时器按 1 秒限流刷新倒计时。
/// </summary>
public sealed class StateCollector
{
    private readonly ILessonsService _lessons;
    private readonly CommandHandler _commandHandler;
    private readonly PluginSettings _settings;
    private readonly ILogger<StateCollector> _logger;
    private readonly Stopwatch _throttle = Stopwatch.StartNew();

    public StateCollector(
        ILessonsService lessons,
        CommandHandler commandHandler,
        PluginSettings settings,
        ILogger<StateCollector> logger)
    {
        _lessons = lessons;
        _commandHandler = commandHandler;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>生成了新的状态快照（转发给局域网/云端）。</summary>
    public event Action<ClassStateSnapshot>? SnapshotPushed;

    /// <summary>课程事件发生（转发给局域网/云端，用于手表通知）。</summary>
    public event Action<ClassEvent>? EventOccurred;

    public void Start()
    {
        _lessons.OnClass += LessonsOnOnClass;
        _lessons.OnBreakingTime += LessonsOnOnBreakingTime;
        _lessons.OnAfterSchool += LessonsOnOnAfterSchool;
        _lessons.CurrentTimeStateChanged += LessonsOnCurrentTimeStateChanged;
        _lessons.PostMainTimerTicked += LessonsOnPostMainTimerTicked;

        PushSnapshot();
        _logger.LogInformation("RemoteCI 状态收集已启动（配对码 {PairCode}）", _settings.PairCode);
    }

    public void Stop()
    {
        _lessons.OnClass -= LessonsOnOnClass;
        _lessons.OnBreakingTime -= LessonsOnOnBreakingTime;
        _lessons.OnAfterSchool -= LessonsOnOnAfterSchool;
        _lessons.CurrentTimeStateChanged -= LessonsOnCurrentTimeStateChanged;
        _lessons.PostMainTimerTicked -= LessonsOnPostMainTimerTicked;
    }

    /// <summary>构建当前状态快照。</summary>
    public ClassStateSnapshot BuildSnapshot()
    {
        var currentSubject = SubjectName(_lessons.CurrentSubject);
        var nextSubject = SubjectName(_lessons.NextClassSubject);

        return new ClassStateSnapshot
        {
            CurrentSubject = currentSubject,
            NextClassSubject = nextSubject,
            CurrentState = MapState(_lessons.CurrentState),
            CurrentTimeLayoutItem = FormatTimeLayoutItem(_lessons.CurrentTimeLayoutItem, currentSubject),
            NextClassTimeLayoutItem = FormatTimeLayoutItem(_lessons.NextClassTimeLayoutItem, nextSubject),
            ClassPlanName = _lessons.CurrentClassPlan?.Name,
            WeekRotation = _commandHandler.WeekOverride ?? ResolveCyclePosition(),
            IsClassPlanEnabled = _lessons.IsClassPlanEnabled,
            IsClassPlanLoaded = _lessons.IsClassPlanLoaded,
            OnClassLeftTime = _lessons.OnClassLeftTime,
            OnBreakingLeftTime = _lessons.OnBreakingTimeLeftTime,
            LessonConfirmed = _lessons.IsLessonConfirmed,
        };
    }

    private void PushSnapshot() => SnapshotPushed?.Invoke(BuildSnapshot());

    private void PushEvent(ClassEventKind kind, string? subject, string message)
    {
        EventOccurred?.Invoke(new ClassEvent
        {
            Event = kind,
            Subject = subject,
            Message = message,
        });
        PushSnapshot(); // 事件后立即刷新一次状态
    }

    private void LessonsOnOnClass(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnClass, SubjectName(_lessons.CurrentSubject), $"上课了：{SubjectName(_lessons.CurrentSubject)}");

    private void LessonsOnOnBreakingTime(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnBreaking, null, "课间休息");

    private void LessonsOnOnAfterSchool(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnAfterSchool, null, "放学啦！");

    private void LessonsOnCurrentTimeStateChanged(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.StateChanged, SubjectName(_lessons.CurrentSubject), "课表状态已更新");

    private void LessonsOnPostMainTimerTicked(object? sender, EventArgs e)
    {
        // 主计时器每 50ms 触发一次，按 1 秒限流推送，保证手表倒计时刷新而不至于过载。
        if (_throttle.ElapsedMilliseconds < 1000)
        {
            return;
        }

        _throttle.Restart();
        PushSnapshot();
    }

    /// <summary>解析多周轮换中当前周的位置（ClassIsland 返回 1-based 位置集合）。</summary>
    private int? ResolveCyclePosition()
    {
        try
        {
            var positions = _lessons.GetCyclePositionsByDate();
            return positions.Count > 0 ? positions[0] : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取周次失败，按无轮换处理");
            return null;
        }
    }

    private static ClassStateKind MapState(TimeState state) => state switch
    {
        TimeState.OnClass => ClassStateKind.Class,
        TimeState.PrepareOnClass => ClassStateKind.Class, // 预留状态，v0.1 近似为上课
        TimeState.Breaking => ClassStateKind.Breaking,
        TimeState.AfterSchool => ClassStateKind.AfterSchool,
        _ => ClassStateKind.None,
    };

    /// <summary>科目显示名；空/后备科目视为无。</summary>
    private static string? SubjectName(Subject? subject)
    {
        if (subject is null)
        {
            return null;
        }

        return subject.Name switch
        {
            "" => null,
            "???" => null,
            var name => name,
        };
    }

    private static string FormatTimeLayoutItem(TimeLayoutItem item, string? subjectName)
    {
        if (item == TimeLayoutItem.Empty)
        {
            return string.Empty;
        }

        var time = $"{item.StartTime:hh\\:mm}-{item.EndTime:hh\\:mm}";
        return item.TimeType switch
        {
            0 => subjectName is null ? time : $"{time} {subjectName}",
            1 => $"{time} {item.BreakNameText}",
            _ => $"{time} {item}",
        };
    }
}
