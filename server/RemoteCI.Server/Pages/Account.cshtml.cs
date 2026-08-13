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
    public IReadOnlyList<DeviceSessionSummary> Sessions { get; private set; } = [];
    public string CurrentVersion => updates.CurrentVersion;
    public ReleaseInfo? LatestRelease { get; private set; }
    public ReleaseAsset? ServerAsset { get; private set; }
    public string? CheckMessage { get; private set; }
    public bool UpdateSucceeded { get; private set; }
    public bool IsFnos => UpdateService.IsFnosRuntime;
    public string? DownloadedFpk { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        Sessions = await identities.ListSessionsAsync(CurrentUser.Id, null, ct);
        if (IsFnos)
        {
            // 刷新页面后仍展示已下载的 fpk（取 updates 目录中最新一个）。
            DownloadedFpk = await FindLatestDownloadedFpkAsync(ct);
        }
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

    /// <summary>检查 GitHub 最新 release（管理员可见）。</summary>
    public async Task<IActionResult> OnPostCheckUpdateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        try
        {
            LatestRelease = await updates.FetchLatestReleaseAsync(ct);
            if (LatestRelease is null)
            {
                CheckMessage = "仓库暂无 release，无法检查更新。";
            }
            else
            {
                ServerAsset = IsFnos
                    ? updates.SelectFnosAsset(LatestRelease)
                    : updates.SelectServerAsset(LatestRelease);
                CheckMessage = UpdateService.IsNewer(
                    UpdateService.NormalizeVersion(LatestRelease.Tag), CurrentVersion)
                    ? "发现新版本，可下载更新。"
                    : "已是最新版本。";
            }
        }
        catch (Exception ex)
        {
            CheckMessage = "检查更新失败：" + ex.Message;
        }
        return Page();
    }

    /// <summary>下载最新服务端包并就地应用，随后触发进程重启（仅管理员）。</summary>
    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        try
        {
            LatestRelease = await updates.FetchLatestReleaseAsync(ct)
                ?? throw new InvalidOperationException("仓库暂无 release，无法更新。");
            var latestVersion = UpdateService.NormalizeVersion(LatestRelease.Tag);
            if (!UpdateService.IsNewer(latestVersion, CurrentVersion))
                throw new InvalidOperationException("已是最新版本，无需更新。");

            var databasePath = Path.IsPathRooted(serverOptions.Value.DatabasePath)
                ? serverOptions.Value.DatabasePath
                : Path.Combine(environment.ContentRootPath, serverOptions.Value.DatabasePath);

            if (IsFnos)
            {
                ServerAsset = updates.SelectFnosAsset(LatestRelease)
                    ?? throw new InvalidOperationException("未找到 fnOS 安装包（fpk），请到 GitHub 手动下载。");
                DownloadedFpk = await updates.DownloadFnosFpkAsync(
                    LatestRelease, ServerAsset, databasePath, environment.ContentRootPath, ct);
                CheckMessage = "最新版 fpk 安装包已下载到数据卷，请在飞牛应用中心选择“手动安装”该安装包完成升级；"
                    + "应用中心会自动拉取新镜像并重建容器。";
                return Page();
            }

            ServerAsset = updates.SelectServerAsset(LatestRelease)
                ?? throw new InvalidOperationException("未找到当前平台的更新包，请到 GitHub 手动下载。");
            await updates.ApplyUpdateAsync(
                LatestRelease, ServerAsset, databasePath, environment.ContentRootPath, ct);

            CheckMessage = $"v{latestVersion} 更新包已应用，服务端即将重启，请稍后刷新页面。";
            UpdateSucceeded = true;
            // 先让响应返回浏览器，再平滑退出进程；容器由 Docker restart 策略自动拉起。
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

    /// <summary>把已下载的 fnOS 安装包回传给浏览器（仅管理员，且文件名限定在 updates 目录内）。</summary>
    public async Task<IActionResult> OnGetDownloadFpkAsync(string file, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        var databasePath = Path.IsPathRooted(serverOptions.Value.DatabasePath)
            ? serverOptions.Value.DatabasePath
            : Path.Combine(environment.ContentRootPath, serverOptions.Value.DatabasePath);
        var updatesRoot = UpdateService.GetUpdatesRoot(databasePath, environment.ContentRootPath);
        var safeName = Path.GetFileName(file);
        var target = Path.Combine(updatesRoot, safeName);
        if (!System.IO.File.Exists(target))
        {
            return NotFound();
        }

        var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "application/octet-stream", safeName);
    }

    private async Task<string?> FindLatestDownloadedFpkAsync(CancellationToken ct)
    {
        var databasePath = Path.IsPathRooted(serverOptions.Value.DatabasePath)
            ? serverOptions.Value.DatabasePath
            : Path.Combine(environment.ContentRootPath, serverOptions.Value.DatabasePath);
        var updatesRoot = UpdateService.GetUpdatesRoot(databasePath, environment.ContentRootPath);
        if (!System.IO.Directory.Exists(updatesRoot))
        {
            return null;
        }

        return await Task.Run(() => System.IO.Directory
            .EnumerateFiles(updatesRoot, "*.fpk")
            .OrderByDescending(path => System.IO.File.GetLastWriteTimeUtc(path))
            .FirstOrDefault(), ct);
    }

    public sealed class PasswordInput
    {
        [Required, StringLength(128, MinimumLength = 8)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 8)]
        public string NewPassword { get; set; } = string.Empty;
    }
}
