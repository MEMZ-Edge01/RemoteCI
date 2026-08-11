using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

public sealed class LoginModel(UserManager<AppUser> users, SignInManager<AppUser> signIn) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { } user)
        {
            var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
            return RedirectToPage(permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.FindByNameAsync(Input.Username.Trim());
        if (user is null || !user.Enabled)
        {
            ModelState.AddModelError(string.Empty, "ID 或密码错误");
            return Page();
        }
        var result = await signIn.PasswordSignInAsync(user, Input.Password, false, true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.IsLockedOut ? "登录失败次数过多，请稍后再试" : "ID 或密码错误");
            return Page();
        }
        var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions);
        return RedirectToPage(permissions.HasFlag(UserPermissions.AccessWebUi) ? "/Index" : "/Account");
    }

    public sealed class LoginInput
    {
        [Required, StringLength(32, MinimumLength = 3)]
        public string Username { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 8)]
        public string Password { get; set; } = string.Empty;
    }
}
