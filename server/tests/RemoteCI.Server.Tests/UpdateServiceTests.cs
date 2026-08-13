using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("v1.0.0-beta.1", "1.0.0-beta.1")]
    public void NormalizeVersion_StripsTagPrefix(string tag, string expected) =>
        Assert.Equal(expected, UpdateService.NormalizeVersion(tag));

    [Theory]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.3.0", false)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    public void IsNewer_ComparesSemanticVersions(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateService.IsNewer(latest, current));

    [Fact]
    public void SelectServerAsset_PicksMatchingPlatformZip()
    {
        var service = new UpdateService();
        var release = new ReleaseInfo(
            "v0.2.0",
            "RemoteCI 0.2.0",
            "",
            new[]
            {
                new ReleaseAsset("RemoteCI.Watch-0.2.0.apk", "https://example/watch.apk", 1),
                new ReleaseAsset("RemoteCI.Plugin-0.2.0.cipx", "https://example/plugin.cipx", 1),
                new ReleaseAsset("RemoteCI.Server-0.2.0-linux-x64.zip", "https://example/linux.zip", 1),
                new ReleaseAsset("RemoteCI.Server-0.2.0-win-x64.zip", "https://example/win.zip", 1),
            });

        var expected = OperatingSystem.IsWindows() ? "win" : "linux";
        var asset = service.SelectServerAsset(release);

        Assert.NotNull(asset);
        Assert.Contains($"-{expected}-", asset.Name);
        Assert.EndsWith(".zip", asset.Name);
    }

    [Fact]
    public void SelectServerAsset_ReturnsNullWhenPlatformMissing()
    {
        var service = new UpdateService();
        var release = new ReleaseInfo(
            "v0.2.0",
            "RemoteCI 0.2.0",
            "",
            new[]
            {
                new ReleaseAsset("RemoteCI.Watch-0.2.0.apk", "https://example/watch.apk", 1),
            });

        Assert.Null(service.SelectServerAsset(release));
    }

    [Fact]
    public void SelectFnosAsset_PicksVersionedFpk()
    {
        var service = new UpdateService();
        var release = new ReleaseInfo(
            "v0.3.0",
            "RemoteCI 0.3.0",
            "",
            new[]
            {
                new ReleaseAsset("RemoteCI.Watch-0.3.0.apk", "https://example/watch.apk", 1),
                new ReleaseAsset("RemoteCI-0.3.0.fpk", "https://example/remoteci.fpk", 1),
                new ReleaseAsset("RemoteCI-0.2.0.fpk", "https://example/old.fpk", 1),
            });

        var asset = service.SelectFnosAsset(release);

        Assert.NotNull(asset);
        Assert.Equal("RemoteCI-0.3.0.fpk", asset.Name);
    }

    [Fact]
    public void SelectFnosAsset_ReturnsNullWhenFpkMissing()
    {
        var service = new UpdateService();
        var release = new ReleaseInfo(
            "v0.3.0",
            "RemoteCI 0.3.0",
            "",
            new[] { new ReleaseAsset("RemoteCI.Server-0.3.0-linux-x64.zip", "https://example/linux.zip", 1) });

        Assert.Null(service.SelectFnosAsset(release));
    }

    [Fact]
    public void GetUpdatesRoot_LivesNextToDatabase()
    {
        // 使用临时目录构造数据库路径，避免 Windows/Linux 对 "C:" 前缀解析不一致。
        var dataDir = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(dataDir, "remoteci.db");

        var root = UpdateService.GetUpdatesRoot(databasePath, "/app");

        Assert.Equal(Path.Combine(dataDir, "updates"), root);
    }
}
