namespace RemoteCI.Server.Services;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string DatabasePath { get; set; } = "data/remoteci.db";
    public string BootstrapAdminUsername { get; set; } = "admin";
    public string? BootstrapAdminPassword { get; set; }
    public string? BootstrapPluginPairCode { get; set; }
    public TimeSpan AccessTokenTtl { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan DeviceSessionTtl { get; set; } = TimeSpan.FromDays(30);

    /// <summary>认证端点（登录/刷新/插件配对）每个来源 IP 每分钟的请求上限。</summary>
    public int AuthRateLimitPerMinute { get; set; } = 20;
}
