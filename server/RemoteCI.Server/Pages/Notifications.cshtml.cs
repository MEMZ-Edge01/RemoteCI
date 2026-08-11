using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class NotificationsModel(UserManager<AppUser> users, PeerRegistry peers) : WebPageModel(users)
{
    [BindProperty]
    public NoticeInput Input { get; set; } = new();
    public bool PluginOnline => peers.HasPlugin;

    public async Task<IActionResult> OnGetAsync() =>
        await RequireAsync(UserPermissions.SendNotifications) is { } denied ? denied : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SendNotifications) is { } denied) return denied;
        if (!ModelState.IsValid) return Page();
        var result = await peers.SendCommandAndWaitAsync(new CommandMessage
        {
            Command = CommandKind.SendNotification,
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
            Notification = new NotificationRequest
            {
                Title = string.IsNullOrWhiteSpace(Input.Title) ? "RemoteCI 通知" : Input.Title.Trim(),
                Message = Input.Message.Trim(),
            },
        }, TimeSpan.FromSeconds(15), ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public sealed class NoticeInput
    {
        [StringLength(60)] public string Title { get; set; } = string.Empty;
        [Required, StringLength(500, MinimumLength = 1)] public string Message { get; set; } = string.Empty;
    }
}
