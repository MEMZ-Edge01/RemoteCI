using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace RemoteCI.Server.Tests;

/// <summary>
/// 测试专用工厂：固定配对码、独立配置，避免影响真实配置。
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestPairCode = "test-pair";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Server:PairCode", TestPairCode);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:PairCode"] = TestPairCode,
            });
        });
    }
}
