using System.Diagnostics;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>高频当前状态与低频七日课表分流收集器。</summary>
public sealed class StateCollector
{
    private readonly ILessonsService _lessons;
    private readonly ScheduleCatalog _schedules;
    private readonly ClassIslandHostControlService _hostControl;
    private readonly ILogger<StateCollector> _logger;
    private readonly Stopwatch _stateThrottle = Stopwatch.StartNew();
    private readonly Stopwatch _scheduleThrottle = Stopwatch.StartNew();
    private string? _lastScheduleSignature;

    public StateCollector(
        ILessonsService lessons,
        ScheduleCatalog schedules,
        ClassIslandHostControlService hostControl,
        ILogger<StateCollector> logger)
    {
        _lessons = lessons;
        _schedules = schedules;
        _hostControl = hostControl;
        _logger = logger;
    }

    public event Action<ClassStateSnapshot>? SnapshotPushed;
    public event Action<ScheduleBundle>? SchedulePushed;
    public event Action<ClassEvent>? EventOccurred;

    public void Start()
    {
        _lessons.OnClass += LessonsOnOnClass;
        _lessons.OnBreakingTime += LessonsOnOnBreakingTime;
        _lessons.OnAfterSchool += LessonsOnOnAfterSchool;
        _lessons.CurrentTimeStateChanged += LessonsOnCurrentTimeStateChanged;
        _lessons.PostMainTimerTicked += LessonsOnPostMainTimerTicked;
        PushSnapshot();
        PushSchedule(force: true);
        _logger.LogInformation("RemoteCI v2 状态与七日课表收集已启动");
    }

    public void Stop()
    {
        _lessons.OnClass -= LessonsOnOnClass;
        _lessons.OnBreakingTime -= LessonsOnOnBreakingTime;
        _lessons.OnAfterSchool -= LessonsOnOnAfterSchool;
        _lessons.CurrentTimeStateChanged -= LessonsOnCurrentTimeStateChanged;
        _lessons.PostMainTimerTicked -= LessonsOnPostMainTimerTicked;
    }

    public ClassStateSnapshot BuildSnapshot()
    {
        var currentSubject = SubjectName(_lessons.CurrentSubject);
        var nextSubject = SubjectName(_lessons.NextClassSubject);
        var hasVolume = _hostControl.TryGetVolumeState(out var volumePercent, out var isMuted);
        return new ClassStateSnapshot
        {
            ScheduleDate = DateTime.Today.ToString("yyyy-MM-dd"),
            CurrentSubject = currentSubject,
            NextClassSubject = nextSubject,
            CurrentState = MapState(_lessons.CurrentState),
            CurrentTimeLayoutItem = FormatTimeLayoutItem(_lessons.CurrentTimeLayoutItem, currentSubject),
            NextClassTimeLayoutItem = FormatTimeLayoutItem(_lessons.NextClassTimeLayoutItem, nextSubject),
            ClassPlanName = _lessons.CurrentClassPlan?.Name,
            IsClassPlanEnabled = _lessons.IsClassPlanEnabled,
            IsClassPlanLoaded = _lessons.IsClassPlanLoaded,
            OnClassLeftTime = _lessons.OnClassLeftTime,
            OnBreakingLeftTime = _lessons.OnBreakingTimeLeftTime,
            LessonConfirmed = _lessons.IsLessonConfirmed,
            IsNotificationPlaying = _hostControl.IsNotificationPlaying,
            IsMainMenuVisible = _hostControl.IsMainMenuVisible,
            IsSleepAvailable = _hostControl.IsSleepAvailable,
            IsHibernateAvailable = _hostControl.IsHibernateAvailable,
            IsVolumeControlAvailable = hasVolume,
            VolumePercent = volumePercent,
            IsMuted = isMuted,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    public ScheduleBundle BuildSchedule() => _schedules.BuildBundle();

    public void ForceSchedulePush()
    {
        PushSchedule(force: true);
        PushEvent(ClassEventKind.ScheduleChanged, null, "课表已更新", pushSnapshot: true);
    }

    public void ForceSnapshotPush() => PushSnapshot();

    private void PushSnapshot() => SnapshotPushed?.Invoke(BuildSnapshot());

    private void PushSchedule(bool force)
    {
        var bundle = BuildSchedule();
        var signature = string.Join('|', bundle.Days.Select(x => $"{x.Date}:{x.Revision}"));
        if (!force && signature == _lastScheduleSignature) return;
        _lastScheduleSignature = signature;
        SchedulePushed?.Invoke(bundle);
    }

    private void PushEvent(ClassEventKind kind, string? subject, string message, bool pushSnapshot = true)
    {
        EventOccurred?.Invoke(new ClassEvent { Event = kind, Subject = subject, Message = message });
        if (pushSnapshot) PushSnapshot();
    }

    private void LessonsOnOnClass(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnClass, SubjectName(_lessons.CurrentSubject), $"上课了：{SubjectName(_lessons.CurrentSubject)}");

    private void LessonsOnOnBreakingTime(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnBreaking, null, "下课休息");

    private void LessonsOnOnAfterSchool(object? sender, EventArgs e) =>
        PushEvent(ClassEventKind.OnAfterSchool, null, "放学啦！");

    private void LessonsOnCurrentTimeStateChanged(object? sender, EventArgs e) => PushSnapshot();

    private void LessonsOnPostMainTimerTicked(object? sender, EventArgs e)
    {
        if (_stateThrottle.ElapsedMilliseconds >= 1000)
        {
            _stateThrottle.Restart();
            PushSnapshot();
        }
        if (_scheduleThrottle.Elapsed >= TimeSpan.FromSeconds(30))
        {
            _scheduleThrottle.Restart();
            PushSchedule(force: false);
        }
    }

    private static ClassStateKind MapState(TimeState state) => state switch
    {
        TimeState.OnClass => ClassStateKind.Class,
        TimeState.PrepareOnClass => ClassStateKind.PrepareClass,
        TimeState.Breaking => ClassStateKind.Breaking,
        TimeState.AfterSchool => ClassStateKind.AfterSchool,
        _ => ClassStateKind.None,
    };

    private static string? SubjectName(Subject? subject) => subject?.Name switch
    {
        null or "" or "???" => null,
        var name => name,
    };

    private static string FormatTimeLayoutItem(TimeLayoutItem item, string? subjectName)
    {
        if (item == TimeLayoutItem.Empty) return string.Empty;
        var time = $"{item.StartTime:hh\\:mm}-{item.EndTime:hh\\:mm}";
        return item.TimeType switch
        {
            0 => subjectName is null ? time : $"{time} {subjectName}",
            1 => $"{time} {item.BreakNameText}",
            _ => $"{time} {item}",
        };
    }
}
