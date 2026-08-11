using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class IndexModel(UserManager<AppUser> users, PeerRegistry peers, IStateStore state, IdentityCoordinator identities)
    : WebPageModel(users)
{
    public bool PluginOnline { get; private set; }
    public int WatchConnections { get; private set; }
    public int AccountCount { get; private set; }
    public ClassStateSnapshot? Snapshot { get; private set; }
    public ScheduleBundle? Schedule { get; private set; }
    public string? PairCode { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRetryConnectionAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        var connected = await peers.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(ct), ct);
        TempData[connected ? "Message" : "Error"] = connected
            ? "插件连接检测成功，账号与权限已重新同步。"
            : "插件仍未连接，ClassIsland 插件会每 5 秒自动重试，请检查插件设置与服务地址。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPairCodeAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi | UserPermissions.ManageUsers) is { } denied) return denied;
        PairCode = await identities.CreatePluginPairingCodeAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        PluginOnline = peers.HasPlugin;
        WatchConnections = peers.WatchCount;
        AccountCount = (await identities.ListUsersAsync(ct)).Count;
        Snapshot = state.GetLatestSnapshot();
        Schedule = state.GetLatestSchedule();
    }
}
