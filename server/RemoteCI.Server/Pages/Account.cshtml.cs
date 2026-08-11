using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class AccountModel(
    UserManager<AppUser> users,
    IdentityCoordinator identities,
    PeerRegistry peers,
    SignInManager<AppUser> signIn) : WebPageModel(users)
{
    [BindProperty]
    public PasswordInput Password { get; set; } = new();
    public IReadOnlyList<DeviceSessionSummary> Sessions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        Sessions = await identities.ListSessionsAsync(CurrentUser.Id, null, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!ModelState.IsValid)
        {
            Sessions = await identities.ListSessionsAsync(CurrentUser.Id, null, ct);
            return Page();
        }
        try
        {
            await identities.ChangePasswordAsync(CurrentUser.Id, new ChangePasswordRequest
            {
                CurrentPassword = Password.CurrentPassword,
                NewPassword = Password.NewPassword,
            }, ct);
            await peers.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(ct), ct);
            await peers.RefreshWatchAuthorizationsAsync(ct);
            await signIn.SignOutAsync();
            TempData["Message"] = "密码已修改，所有设备会话已撤销，请重新登录。";
            return RedirectToPage("/Login");
        }
        catch (IdentityOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Sessions = await identities.ListSessionsAsync(CurrentUser.Id, null, ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try
        {
            await identities.RevokeSessionAsync(CurrentUser.Id, id, ct);
            await peers.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(ct), ct);
            await peers.RefreshWatchAuthorizationsAsync(ct);
            TempData["Message"] = "设备会话已撤销。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public sealed class PasswordInput
    {
        [Required, StringLength(128, MinimumLength = 8)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 8)]
        public string NewPassword { get; set; } = string.Empty;
    }
}
