using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class ScheduleModel(UserManager<AppUser> users, IStateStore state, PeerRegistry peers) : WebPageModel(users)
{
    [BindProperty]
    public ScheduleInput Input { get; set; } = new();
    public ScheduleBundle? Bundle { get; private set; }
    public bool PluginOnline => peers.HasPlugin;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync(UserPermissions.ManageSchedule) is { } denied) return denied;
        Bundle = state.GetLatestSchedule();
        return Page();
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
