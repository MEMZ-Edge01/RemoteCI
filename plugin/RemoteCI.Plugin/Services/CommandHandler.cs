using System.Globalization;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>全部远程写操作的唯一执行入口；只返回 ClassIsland 实际执行后的结果。</summary>
public sealed class CommandHandler
{
    private readonly IProfileService _profiles;
    private readonly ILessonsService _lessons;
    private readonly ScheduleCatalog _schedules;
    private readonly RemoteNotificationProvider _notifications;
    private readonly ClassIslandHostControlService _hostControl;
    private readonly ILogger<CommandHandler> _logger;

    public CommandHandler(
        IProfileService profiles,
        ILessonsService lessons,
        ScheduleCatalog schedules,
        ClassIslandHostControlService hostControl,
        IEnumerable<IHostedService> hostedServices,
        ILogger<CommandHandler> logger)
    {
        _profiles = profiles;
        _lessons = lessons;
        _schedules = schedules;
        _hostControl = hostControl;
        _notifications = hostedServices.OfType<RemoteNotificationProvider>().Single();
        _logger = logger;
    }

    public event Action<ClassEvent>? NotificationSent;
    public event Action? ScheduleChanged;
    public event Action? HostStateChanged;

    public async Task<CommandResult> HandleAsync(CommandMessage command)
    {
        var required = CommandPermissions.Required(command.Command);
        if (required == UserPermissions.None)
            return Failure(CommandResultCodes.InvalidRequest, $"未知指令：{command.Command}");
        if (command.RequestedBy is null || !command.RequestedBy.Permissions.HasFlag(required))
            return Failure(CommandResultCodes.Forbidden, "权限不足");

        try
        {
            return command.Command switch
            {
                CommandKind.ChangeSchedule => await HandleScheduleChangeAsync(command.ScheduleChange),
                CommandKind.SendNotification => await HandleNotificationAsync(
                    command.Notification,
                    GetNotificationSenderName(command.RequestedBy)),
                CommandKind.ClearNotifications => await HandleClearNotificationsAsync(),
                CommandKind.SetMainMenuVisibility => await HandleMainMenuVisibilityAsync(command.MainMenuVisible),
                CommandKind.Power => HandlePowerAction(command.PowerAction),
                _ => Failure(CommandResultCodes.InvalidRequest, $"未知指令：{command.Command}"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoteCI 指令执行失败：{Command}", command.Command);
            return Failure(CommandResultCodes.InternalError, "指令执行异常，请查看 ClassIsland 日志");
        }
    }

    private async Task<CommandResult> HandleScheduleChangeAsync(ScheduleChangeRequest? request)
    {
        if (request is null || !TryParseDate(request.Date, out var date))
            return Failure(CommandResultCodes.InvalidRequest, "换课日期无效");
        if (date < DateTime.Today || date >= DateTime.Today.AddDays(7))
            return Failure(CommandResultCodes.InvalidRequest, "只能修改今天起未来七天的课表");
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision))
            return Failure(CommandResultCodes.InvalidRequest, "缺少课表修订号");

        var result = await Dispatcher.UIThread.InvokeAsync(() => ApplyScheduleChange(date, request));
        if (result.Success) ScheduleChanged?.Invoke();
        return result;
    }

    private CommandResult ApplyScheduleChange(DateTime date, ScheduleChangeRequest request)
    {
        var before = _schedules.BuildDay(date);
        if (!before.Enabled)
            return Failure(CommandResultCodes.ScheduleUnavailable, $"{date:yyyy-MM-dd} 没有可编辑课表");
        if (!string.Equals(before.Revision, request.ExpectedRevision, StringComparison.Ordinal))
            return new CommandResult
            {
                Success = false,
                Code = CommandResultCodes.ScheduleStale,
                Message = "课表已被其他管理者修改，请刷新后重新确认",
                ScheduleRevision = before.Revision,
            };

        var validationError = ScheduleMutation.Validate(
            before.Courses.Count,
            request,
            subjectId => _profiles.Profile.Subjects.ContainsKey(subjectId));
        if (validationError is not null)
            return Failure(CommandResultCodes.InvalidRequest, validationError);

        var plan = GetWritablePlan(date);
        if (plan is null)
            return Failure(CommandResultCodes.ScheduleUnavailable, $"{date:yyyy-MM-dd} 无法创建临时课表层");
        validationError = ScheduleMutation.Validate(
            plan.Classes.Count,
            request,
            subjectId => _profiles.Profile.Subjects.ContainsKey(subjectId));
        if (validationError is not null)
            return Failure(CommandResultCodes.InvalidRequest, validationError);

        var mutation = ScheduleMutation.Create(plan.Classes, request);
        mutation.Apply();

        try
        {
            _profiles.SaveProfile();
        }
        catch (Exception ex)
        {
            mutation.Rollback();
            _logger.LogError(ex, "保存 {Date} 临时课表失败", date);
            return Failure(CommandResultCodes.SaveFailed, "ClassIsland 保存课表失败，操作未确认");
        }

        var after = _schedules.BuildDay(date);
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = request.Mode == ScheduleChangeMode.Exchange ? "两节课程已临时交换" : "课程已临时替换",
            ScheduleRevision = after.Revision,
        };
    }

    private async Task<CommandResult> HandleNotificationAsync(NotificationRequest? request, string senderName)
    {
        var message = request?.Message.Trim();
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
            return Failure(CommandResultCodes.InvalidRequest, "通知正文需为 1-500 个字符");
        var title = BuildNotificationTitle(senderName, request?.Title);

        await _notifications.ShowRemoteNotificationAsync(
            title,
            message,
            request!.IsNotificationEffectEnabled,
            request.IsNotificationSoundEnabled,
            request.IsSpeechEnabled);
        NotificationSent?.Invoke(new ClassEvent
        {
            Event = ClassEventKind.Custom,
            Subject = title,
            Message = message,
        });
        return Success("通知已在 ClassIsland 显示并广播到在线手表");
    }

    private async Task<CommandResult> HandleClearNotificationsAsync()
    {
        if (!_hostControl.IsNotificationPlaying) return Success("ClassIsland 当前没有提醒");
        await _hostControl.ClearNotificationsAsync();
        HostStateChanged?.Invoke();
        return Success("已清除 ClassIsland 提醒");
    }

    private async Task<CommandResult> HandleMainMenuVisibilityAsync(bool? visible)
    {
        if (visible is null) return Failure(CommandResultCodes.InvalidRequest, "缺少主界面显隐状态");
        await _hostControl.SetMainMenuVisibilityAsync(visible.Value);
        HostStateChanged?.Invoke();
        return Success(visible.Value ? "已显示 ClassIsland 主界面" : "已隐藏 ClassIsland 主界面");
    }

    private CommandResult HandlePowerAction(PowerActionKind? action)
    {
        if (action is null || !Enum.IsDefined(action.Value))
            return Failure(CommandResultCodes.InvalidRequest, "电源操作无效");
        _hostControl.SchedulePowerAction(action.Value);
        return Success(action.Value switch
        {
            PowerActionKind.Shutdown => "Windows 即将关机",
            PowerActionKind.Restart => "Windows 即将重启",
            PowerActionKind.Sleep => "Windows 即将进入睡眠",
            PowerActionKind.Hibernate => "Windows 即将进入休眠",
            _ => "电源操作已提交",
        });
    }

    /// <summary>在最终执行端统一添加署名，客户端无法通过自定义标题绕过。</summary>
    internal static string GetNotificationSenderName(UserProfile requestedBy) => requestedBy.DisplayName.Trim();

    internal static string BuildNotificationTitle(string senderName, string? requestedTitle)
    {
        var title = requestedTitle?.Trim();
        title = string.IsNullOrWhiteSpace(title) ? "RemoteCI 通知" : title[..Math.Min(title.Length, 60)];
        return $"由{senderName.Trim()}发送：{title}";
    }

    private ClassIsland.Shared.Models.Profile.ClassPlan? GetWritablePlan(DateTime date)
    {
        var plan = _lessons.GetClassPlanByDate(date, out var planId);
        if (plan is null || planId is null) return null;
        if (plan.IsOverlay) return plan;
        var overlayId = _profiles.CreateTempClassPlan(planId.Value, enableDateTime: date);
        if (overlayId is null) return null;
        return _lessons.GetClassPlanByDate(date, out var refreshedId) is { IsOverlay: true } refreshed
            ? refreshed
            : _profiles.Profile.ClassPlans.GetValueOrDefault(refreshedId ?? overlayId.Value);
    }

    private static bool TryParseDate(string value, out DateTime date) => DateTime.TryParseExact(
        value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };

    private static CommandResult Failure(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };
}
