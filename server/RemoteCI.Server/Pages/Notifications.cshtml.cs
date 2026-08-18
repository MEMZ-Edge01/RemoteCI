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
public sealed class NotificationsModel(
    UserManager<AppUser> users,
    PeerRegistry peers,
    IdentityCoordinator identities) : WebPageModel(users)
{
    [BindProperty]
    public NoticeInput Input { get; set; } = new();

    /// <summary>全局“强制在标题显示发送人”开关，作用于 WebUI 与手表的所有通知。</summary>
    [BindProperty]
    public bool ForceSenderInTitle { get; set; }

    public bool PluginOnline => peers.HasPlugin;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await RequireAsync(UserPermissions.SendNotifications) is { } denied) return denied;
        // 保留旧地址兼容书签，实际表单已经合并到控制页。
        return LocalRedirect($"{Url.Page("/Control")}#send-notification");
    }

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
                // 空正文发送为空字符串，由插件以原标题兜底，避免空值绑定导致异常。
                Message = Input.Message?.Trim() ?? string.Empty,
                // 署名是否强制由服务端全局设置决定，避免客户端绕过。
                ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(ct),
            },
        }, TimeSpan.FromSeconds(15), ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSettingsAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.SendNotifications) is { } denied) return denied;
        var settings = await identities.SetForceSenderInTitleAsync(ForceSenderInTitle, ct);
        await peers.SendSettingsToWatchesAsync(settings, ct);
        TempData["Message"] = ForceSenderInTitle ? "已开启强制显示发送人" : "已关闭强制显示发送人";
        return RedirectToPage();
    }

    public sealed class NoticeInput
    {
        [StringLength(60)] public string? Title { get; set; }
        [StringLength(500)] public string? Message { get; set; }
    }
}
