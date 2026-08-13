using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class AccountModel(
    UserManager<AppUser> users,
    IdentityCoordinator identities,
    PeerRegistry peers,
    SignInManager<AppUser> signIn,
    UpdateService updates,
    IOptions<ServerOptions> serverOptions,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime) : WebPageModel(users)
{
    [BindProperty]
    public PasswordInput Password { get; set; } = new();
    [BindProperty]
    public UpdateInput UpdateOptions { get; set; } = new();
    public IReadOnlyList<DeviceSessionSummary> Sessions { get; private set; } = [];
    public string CurrentVersion => updates.CurrentVersion;
    public ReleaseInfo? LatestRelease { get; private set; }
    public ReleaseAsset? ServerAsset { get; private set; }
    public string? CheckMessage { get; private set; }
    public bool UpdateSucceeded { get; private set; }
    public bool IsFnos => UpdateService.IsFnosRuntime;
    public bool IsDevelopment => environment.IsDevelopment();
    public UpdateChannel SelectedUpdateChannel =>
        UpdateOptions.Channel == UpdateChannel.Beta ? UpdateChannel.Beta : UpdateChannel.Stable;

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

    /// <summary>按所选渠道检查 GitHub release（管理员可见）。</summary>
    public async Task<IActionResult> OnPostCheckUpdateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        RemovePasswordValidationErrors();
        if (!UpdateService.CanSelfUpdate(IsDevelopment, IsFnos))
        {
            CheckMessage = IsFnos
                ? UpdateService.FnosManagedMessage
                : UpdateService.DevelopmentManagedMessage;
            return Page();
        }
        try
        {
            LatestRelease = await updates.FetchLatestReleaseAsync(SelectedUpdateChannel, ct);
            if (LatestRelease is null)
            {
                CheckMessage = "仓库暂无 release，无法检查更新。";
            }
            else
            {
                ServerAsset = updates.SelectServerAsset(LatestRelease);
                var latestVersion = UpdateService.NormalizeVersion(LatestRelease.Tag);
                CheckMessage = UpdateService.IsNewer(latestVersion, CurrentVersion)
                    ? "发现新版本，可下载更新。"
                    : UpdateService.CanInstall(latestVersion, CurrentVersion, UpdateOptions.Force)
                        ? "当前版本与渠道最新版本相同，可强制重新下载并覆盖安装。"
                        : UpdateService.CompareVersions(latestVersion, CurrentVersion) < 0
                            ? "当前版本高于所选渠道的最新版本，拒绝降级。"
                            : "已是最新版本，可启用强制更新重新下载安装。";
            }
        }
        catch (Exception ex)
        {
            CheckMessage = "检查更新失败：" + ex.Message;
        }
        return Page();
    }

    /// <summary>下载所选渠道的服务端包并就地应用，随后触发进程重启（仅管理员）。</summary>
    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        RemovePasswordValidationErrors();
        if (!UpdateService.CanSelfUpdate(IsDevelopment, IsFnos))
        {
            CheckMessage = IsFnos
                ? UpdateService.FnosManagedMessage
                : UpdateService.DevelopmentManagedMessage;
            return Page();
        }
        try
        {
            LatestRelease = await updates.FetchLatestReleaseAsync(SelectedUpdateChannel, ct)
                ?? throw new InvalidOperationException("仓库暂无 release，无法更新。");
            var latestVersion = UpdateService.NormalizeVersion(LatestRelease.Tag);
            if (!UpdateService.CanInstall(latestVersion, CurrentVersion, UpdateOptions.Force))
                throw new InvalidOperationException(
                    UpdateService.CompareVersions(latestVersion, CurrentVersion) < 0
                        ? "当前版本高于所选渠道的最新版本，拒绝降级。"
                        : "已是最新版本；如需覆盖安装，请启用强制更新。");

            var databasePath = Path.IsPathRooted(serverOptions.Value.DatabasePath)
                ? serverOptions.Value.DatabasePath
                : Path.Combine(environment.ContentRootPath, serverOptions.Value.DatabasePath);

            ServerAsset = updates.SelectServerAsset(LatestRelease)
                ?? throw new InvalidOperationException("未找到当前平台的更新包，请到 GitHub 手动下载。");
            var prepared = await updates.PrepareUpdateAsync(
                LatestRelease, ServerAsset, databasePath, environment.ContentRootPath, ct);
            // ContentRoot 在 VS 调试时是源码目录；正式更新必须只替换实际程序集目录。
            var installDirectory = UpdateService.ResolveInstallDirectory(AppContext.BaseDirectory);
            var mode = await updates.BeginApplyAsync(prepared, installDirectory, ct);

            CheckMessage = mode == UpdateApplyMode.ExternalInstaller
                ? $"v{latestVersion} 更新包已准备，服务端退出后将完成替换并自动重启，请稍后刷新页面。"
                : $"v{latestVersion} 更新包已应用，服务端即将重启，请稍后刷新页面。";
            UpdateSucceeded = true;
            // 外部安装器已经等待当前 PID；容器则由 restart 策略拉起。
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                lifetime.StopApplication();
            });
        }
        catch (Exception ex)
        {
            CheckMessage = "更新失败：" + ex.Message;
        }
        return Page();
    }

    /// <summary>
    /// 账号页包含互相独立的密码与更新表单；更新 POST 不应显示空密码字段的自动校验错误。
    /// </summary>
    private void RemovePasswordValidationErrors()
    {
        var prefix = nameof(Password);
        foreach (var key in ModelState.Keys.Where(key =>
                     key.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals(nameof(PasswordInput.CurrentPassword), StringComparison.OrdinalIgnoreCase) ||
                     key.Equals(nameof(PasswordInput.NewPassword), StringComparison.OrdinalIgnoreCase)).ToArray())
            ModelState.Remove(key);
    }

    public sealed class PasswordInput
    {
        [Required, StringLength(128, MinimumLength = 8)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 8)]
        public string NewPassword { get; set; } = string.Empty;
    }

    public sealed class UpdateInput
    {
        public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
        public bool Force { get; set; }
    }
}
