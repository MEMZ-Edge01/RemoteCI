using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
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
        var id = Users.GetUserId(User);
        var user = id is not null && Guid.TryParse(id, out var userId)
            ? await Users.Users.Include(x => x.RoleDefinition).SingleOrDefaultAsync(x => x.Id == userId)
            : null;
        if (user is null || !user.Enabled) return RedirectToPage("/Login");
        CurrentUser = user;
        Permissions = RolePermissions.Effective(
            user.Role,
            user.GrantedPermissions,
            user.RoleDefinition.DefaultPermissions);
        return permission is not null && !Permissions.HasFlag(permission.Value)
            ? RedirectToPage("/Denied")
            : null;
    }
}
