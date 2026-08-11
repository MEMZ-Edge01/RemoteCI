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
}
