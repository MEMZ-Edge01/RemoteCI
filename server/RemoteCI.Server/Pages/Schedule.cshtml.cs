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
    SchedulePullSettings pullSettings) : WebPageModel(users)
{
    [BindProperty]
    public ScheduleInput Input { get; set; } = new();
    public ScheduleBundle? Bundle { get; private set; }
    public bool PluginOnline => peers.HasPlugin;
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
        var sent = await peers.RequestSchedulePullAsync(ct);
        TempData[sent ? "Message" : "Error"] = sent
            ? "已请求插件重新生成七日课表。"
            : "插件未在线，无法拉取课表。";
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
        var selection = Input.SourceSelection.Split('|');
        if (selection.Length != 3 || !int.TryParse(selection[2], out var sourceIndex))
        {
            TempData["Error"] = "请选择有效的日期和原节次。";
            return RedirectToPage();
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
                Date = selection[0],
                Mode = Input.Mode,
                SourceIndex = sourceIndex,
                TargetIndex = Input.TargetIndex,
                ReplacementSubjectId = Input.ReplacementSubjectId,
                ExpectedRevision = selection[1],
            },
        }, TimeSpan.FromSeconds(15), ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public sealed class ScheduleInput
    {
        public string SourceSelection { get; set; } = string.Empty;
        public ScheduleChangeMode Mode { get; set; } = ScheduleChangeMode.Exchange;
        public int? TargetIndex { get; set; }
        public Guid? ReplacementSubjectId { get; set; }
    }
}
