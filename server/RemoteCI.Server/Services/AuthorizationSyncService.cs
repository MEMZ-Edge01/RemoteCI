namespace RemoteCI.Server.Services;

/// <summary>
/// 把最新账号授权镜像推送给插件，并立即刷新所有在线手表的缓存授权。
/// 所有账号、角色和扩展策略变更都通过此入口同步，避免漏掉任一消费者。
/// </summary>
public sealed class AuthorizationSyncService(IdentityCoordinator identities, PeerRegistry peers)
{
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await peers.SendAccountSyncToPluginsAsync(await identities.CreateSyncAsync(ct), ct);
        await peers.RefreshWatchAuthorizationsAsync(ct);
    }
}
