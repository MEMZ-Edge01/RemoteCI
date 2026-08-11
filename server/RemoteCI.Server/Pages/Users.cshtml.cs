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
public sealed class UsersModel(UserManager<AppUser> users, IdentityCoordinator identities, PeerRegistry peers)
    : WebPageModel(users)
{
    [BindProperty]
    public UserInput Create { get; set; } = new();
    [BindProperty]
    public UserInput Edit { get; set; } = new();
    public IReadOnlyList<UserListItem> Accounts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        try
        {
            await identities.CreateUserAsync(new CreateUserRequest
            {
                Username = Create.Username,
                DisplayName = Create.DisplayName,
                Password = Create.Password,
                Role = Create.Role,
                GrantedPermissions = Create.Grants,
            }, ct);
            await SyncAsync(ct);
            TempData["Message"] = "账号已创建。";
            return RedirectToPage();
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; return RedirectToPage(); }
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        try
        {
            await identities.UpdateUserAsync(Edit.Id, new UpdateUserRequest
            {
                DisplayName = Edit.DisplayName,
                Role = Edit.Role,
                Enabled = Edit.Enabled,
                GrantedPermissions = Edit.Grants,
            }, ct);
            await SyncAsync(ct);
            TempData["Message"] = "账号与权限已更新。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(Guid id, string password, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        try
        {
            await identities.ResetPasswordAsync(id, password, ct);
            await SyncAsync(ct);
            TempData["Message"] = "密码已重置，该用户的设备会话已全部撤销。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        try
        {
            await identities.DeleteUserAsync(id, ct);
            await SyncAsync(ct);
            TempData["Message"] = "账号已删除。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    private async Task<IActionResult> LoadAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        Accounts = await identities.ListUsersAsync(ct);
        return Page();
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        await peers.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(ct), ct);
        await peers.RefreshWatchAuthorizationsAsync(ct);
    }

    public sealed class UserInput
    {
        public Guid Id { get; set; }
        [StringLength(32, MinimumLength = 3)] public string Username { get; set; } = string.Empty;
        [StringLength(40, MinimumLength = 1)] public string DisplayName { get; set; } = string.Empty;
        [StringLength(128, MinimumLength = 8)] public string Password { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.User;
        public bool Enabled { get; set; }
        public bool AccessWebUi { get; set; }
        public bool ManageUsers { get; set; }
        public bool SendNotifications { get; set; }
        public bool ManageSchedule { get; set; }
        public bool SystemControl { get; set; }
        public UserPermissions Grants =>
            (AccessWebUi ? UserPermissions.AccessWebUi : 0) |
            (ManageUsers ? UserPermissions.ManageUsers : 0) |
            (SendNotifications ? UserPermissions.SendNotifications : 0) |
            (ManageSchedule ? UserPermissions.ManageSchedule : 0) |
            (SystemControl ? UserPermissions.SystemControl : 0);
    }
}
