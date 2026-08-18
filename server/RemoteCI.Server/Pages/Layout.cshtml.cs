using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class LayoutModel(UserManager<AppUser> users, UserCardLayoutService layouts) : WebPageModel(users)
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnGetGetAsync(string pageKey, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try { return new JsonResult(await layouts.GetAsync(CurrentUser.Id, pageKey, ct)); }
        catch (IdentityOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string pageKey,
        string layoutJson,
        CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try { return new JsonResult(await layouts.SaveAsync(CurrentUser.Id, pageKey, layoutJson, ct)); }
        catch (IdentityOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    public async Task<IActionResult> OnPostResetAsync(string pageKey, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try { await layouts.ResetAsync(CurrentUser.Id, pageKey, ct); return new JsonResult(CardLayoutDocument.Empty()); }
        catch (IdentityOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
