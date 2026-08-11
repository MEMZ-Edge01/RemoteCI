using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

public abstract class WebPageModel(UserManager<AppUser> users) : PageModel
{
    protected UserManager<AppUser> Users { get; } = users;
    public AppUser CurrentUser { get; private set; } = null!;
    public UserPermissions Permissions { get; private set; }

    protected async Task<IActionResult?> RequireAsync(UserPermissions? permission = null)
    {
        var user = await Users.GetUserAsync(User);
        if (user is null || !user.Enabled) return RedirectToPage("/Login");
        CurrentUser = user;
        Permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
        return permission is not null && !Permissions.HasFlag(permission.Value)
            ? RedirectToPage("/Denied")
            : null;
    }
}
