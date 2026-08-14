using System.Globalization;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>全部远程写操作的唯一执行入口；只返回 ClassIsland 实际执行后的结果。</summary>
public sealed class CommandHandler
{
    private readonly ScheduleCatalog _schedules;
    private readonly IScheduleBackend _scheduleBackend;
    private readonly IProfileWriteOperations _profileOps;
    private readonly RemoteNotificationProvider _notifications;
    private readonly ClassIslandHostControlService _hostControl;
    private readonly ILogger _logger;
    private readonly ExtensionCommandRouter _extensionRouter;

    public CommandHandler(
        ScheduleCatalog schedules,
        IScheduleBackend scheduleBackend,
        IProfileWriteOperations profileOps,
        ClassIslandHostControlService hostControl,
        IEnumerable<IHostedService> hostedServices,
        IRemoteCiExtensionRegistry extensions,
        ILoggerFactory loggerFactory)
    {
        _schedules = schedules;
        _scheduleBackend = scheduleBackend;
        _profileOps = profileOps;
        _hostControl = hostControl;
        _notifications = hostedServices.OfType<RemoteNotificationProvider>().Single();
        _logger = loggerFactory.CreateLogger<CommandHandler>();
        _extensionRouter = new ExtensionCommandRouter(extensions, loggerFactory);
    }

    public event Action<ClassEvent>? NotificationSent;
    public event Action? ScheduleChanged;
    public event Action? HostStateChanged;

    /// <summary>插件停止时取消尚未执行的睡眠/休眠电源操作。</summary>
    public void CancelPendingPowerActions() => _hostControl.CancelPendingPowerActions();

    public async Task<CommandResult> HandleAsync(CommandMessage command)
    {
        // 扩展命令的权限随注册项动态声明，不走静态权限表。
        if (command.Command == CommandKind.RunExtension)
            return await _extensionRouter.RunAsync(command);

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
                CommandKind.Volume => HandleVolume(command.Volume),
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
        if (ValidateScheduleChangeRequest(request, DateTime.Today, out var date) is { } validationError)
            return Failure(CommandResultCodes.InvalidRequest, validationError);

        var result = await Dispatcher.UIThread.InvokeAsync(() => ApplyScheduleChange(date, request!));
        if (result.Success) ScheduleChanged?.Invoke();
        return result;
    }

    /// <summary>换课请求的形态校验（纯逻辑，先于 UI 线程执行）；通过返回 null 并给出解析后的日期。</summary>
    internal static string? ValidateScheduleChangeRequest(
        ScheduleChangeRequest? request, DateTime today, out DateTime date)
    {
        date = default;
        if (request is null || !TryParseDate(request.Date, out date))
            return "换课日期无效";
        if (date < today || date >= today.AddDays(7))
            return "只能修改今天起未来七天的课表";
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision))
            return "缺少课表修订号";
        return null;
    }

    private CommandResult ApplyScheduleChange(DateTime date, ScheduleChangeRequest request) =>
        ScheduleChangeExecutor.Apply(
            date, request, _schedules, _scheduleBackend, _profileOps,
            ex => _logger.LogError(ex, "保存 {Date} 临时课表失败", date));

    private async Task<CommandResult> HandleNotificationAsync(NotificationRequest? request, string senderName)
    {
        if (request is null)
            return Failure(CommandResultCodes.InvalidRequest, "缺少通知内容");
        var message = request.Message.Trim();
        if (message.Length > 500)
            return Failure(CommandResultCodes.InvalidRequest, "通知正文不能超过 500 个字符");
        // 服务端会按全局设置注入 ForceSenderInTitle；null 按旧行为视为开启。
        var title = BuildNotificationTitle(senderName, request.Title, request.ForceSenderInTitle != false);
        // 标题与正文均可留空；正文留空时以原标题兜底，避免 ClassIsland 显示空白正文。
        if (string.IsNullOrWhiteSpace(message)) message = NormalizeNotificationTitle(request.Title);

        await _notifications.ShowRemoteNotificationAsync(
            title,
            message,
            request.IsNotificationEffectEnabled,
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

    private CommandResult HandleVolume(VolumeControlRequest? request)
    {
        if (request is null || request is { Level: null, Muted: null })
            return Failure(CommandResultCodes.InvalidRequest, "缺少音量控制参数");
        if (request.Level is < 0 or > 100)
            return Failure(CommandResultCodes.InvalidRequest, "音量必须在 0-100 之间");

        _hostControl.SetVolume(request.Level, request.Muted);
        HostStateChanged?.Invoke();
        return Success(request.Muted switch
        {
            true => "电脑已静音",
            false => "电脑已取消静音",
            _ => $"电脑音量已调至 {request.Level}%",
        });
    }

    /// <summary>在最终执行端统一添加署名，是否强制由服务端全局设置决定。</summary>
    internal static string GetNotificationSenderName(UserProfile requestedBy) => requestedBy.DisplayName.Trim();

    internal static string BuildNotificationTitle(string senderName, string? requestedTitle, bool forceSenderInTitle = true)
    {
        var title = NormalizeNotificationTitle(requestedTitle);
        return forceSenderInTitle ? $"由{senderName.Trim()}发送：{title}" : title;
    }

    /// <summary>标题留空时统一使用默认标题，并负责 60 字截断与首尾空白清理。</summary>
    internal static string NormalizeNotificationTitle(string? requestedTitle)
    {
        var title = requestedTitle?.Trim();
        return string.IsNullOrWhiteSpace(title) ? "RemoteCI 通知" : title[..Math.Min(title.Length, 60)];
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
