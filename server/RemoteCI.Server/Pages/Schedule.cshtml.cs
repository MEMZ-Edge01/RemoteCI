using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class ScheduleModel(
    UserManager<AppUser> users,
    IStateStore state,
    PeerRegistry peers,
    SchedulePullSettings pullSettings,
    ScheduleSyncService scheduleSync) : WebPageModel(users)
{
    [BindProperty]
    public ScheduleInput Input { get; set; } = new();
    public ScheduleBundle? Bundle { get; private set; }
    public bool PluginOnline => peers.HasPlugin;
    public ScheduleSyncStatus? CurrentTask => scheduleSync.Current;
    [BindProperty]
    public SchedulePullInterval PullInterval { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync(UserPermissions.ManageSchedule) is { } denied) return denied;
        Bundle = state.GetLatestSchedule();
        PullInterval = await pullSettings.GetIntervalAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPullAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageSchedule) is { } denied) return denied;
        var status = await scheduleSync.StartAndWaitAsync(ScheduleSyncSource.WebUi, ct);
        if (status.State == ScheduleSyncTaskState.Completed)
            TempData["Message"] = "已从插件拉取最新课表，并强制覆盖服务端缓存。";
        else
            TempData["Error"] = status.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPullIntervalAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageSchedule) is { } denied) return denied;
        if (!Enum.IsDefined(PullInterval))
        {
            TempData["Error"] = "请选择有效的自动拉取间隔。";
            return RedirectToPage();
        }
        await pullSettings.SetIntervalAsync(PullInterval, ct);
        TempData["Message"] = PullInterval == SchedulePullInterval.Disabled
            ? "已关闭定时拉取课表。"
            : "自动拉取课表间隔已保存。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageSchedule) is { } denied) return denied;
        var sourceDay = state.GetLatestSchedule()?.Days.FirstOrDefault(day =>
            day.Enabled &&
            string.Equals(day.Date, Input.Date, StringComparison.Ordinal) &&
            string.Equals(day.Revision, Input.ExpectedRevision, StringComparison.Ordinal));
        var sourceCourse = sourceDay?.Courses.FirstOrDefault(course =>
            course.Enabled && course.Index == Input.SourceIndex);
        if (sourceDay is null || sourceCourse is null)
        {
            TempData["Error"] = "请选择有效的日期和原节次。";
            return RedirectToPage();
        }

        int? targetIndex = null;
        if (Input.Mode == ScheduleChangeMode.Exchange)
        {
            var targetCourse = sourceDay.Courses.FirstOrDefault(course =>
                course.Enabled && course.Index == Input.TargetIndex);
            if (targetCourse is null || targetCourse.Index == sourceCourse.Index)
            {
                TempData["Error"] = "请选择与原节次同一天的其他目标节次。";
                return RedirectToPage();
            }
            targetIndex = targetCourse.Index;
        }
        var result = await peers.SendCommandAndWaitAsync(new CommandMessage
        {
            Command = CommandKind.ChangeSchedule,
            RequestedBy = new UserProfile
            {
                Id = CurrentUser.Id,
                Username = CurrentUser.UserName!,
                DisplayName = CurrentUser.DisplayName,
                Role = CurrentUser.Role,
                GrantedPermissions = CurrentUser.GrantedPermissions,
                Permissions = Permissions,
                Version = CurrentUser.Version,
            },
            ScheduleChange = new ScheduleChangeRequest
            {
                Date = sourceDay.Date,
                Mode = Input.Mode,
                SourceIndex = sourceCourse.Index,
                TargetIndex = targetIndex,
                ReplacementSubjectId = Input.Mode == ScheduleChangeMode.Replace ? Input.ReplacementSubjectId : null,
                ExpectedRevision = sourceDay.Revision,
            },
        }, TimeSpan.FromSeconds(15), ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public sealed class ScheduleInput
    {
        public string Date { get; set; } = string.Empty;
        public string ExpectedRevision { get; set; } = string.Empty;
        public int? SourceIndex { get; set; }
        public ScheduleChangeMode Mode { get; set; } = ScheduleChangeMode.Exchange;
        public int? TargetIndex { get; set; }
        public Guid? ReplacementSubjectId { get; set; }
    }
}
