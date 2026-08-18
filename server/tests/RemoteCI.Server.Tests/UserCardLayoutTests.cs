using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class UserCardLayoutTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public UserCardLayoutTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LayoutsAreScopedByAccountAndPageAndCanBeReset()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var layouts = scope.ServiceProvider.GetRequiredService<UserCardLayoutService>();
        var adminId = await db.Users.Where(user => user.UserName == TestWebApplicationFactory.AdminUsername)
            .Select(user => user.Id).SingleAsync();
        var other = await identities.CreateUserAsync(new CreateUserRequest
        {
            Username = "layout.other",
            DisplayName = "Layout other",
            Password = "Layout-Other-Password-2026",
        });
        var json = """{"version":1,"items":[{"cardId":"card-a","groupId":"main","order":0,"span":2}]}""";

        var saved = await layouts.SaveAsync(adminId, "index", json);
        Assert.Equal(2, Assert.Single(saved.Items).Span);
        Assert.Single((await layouts.GetAsync(adminId, "index")).Items);
        Assert.Empty((await layouts.GetAsync(other.Id, "index")).Items);
        Assert.Empty((await layouts.GetAsync(adminId, "control")).Items);

        await layouts.ResetAsync(adminId, "index");
        Assert.Empty((await layouts.GetAsync(adminId, "index")).Items);
    }

    [Fact]
    public async Task InvalidPageDuplicateCardsAndInvalidSpanAreRejected()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var layouts = scope.ServiceProvider.GetRequiredService<UserCardLayoutService>();
        var userId = await db.Users.Select(user => user.Id).FirstAsync();

        await Assert.ThrowsAsync<IdentityOperationException>(() => layouts.SaveAsync(
            userId, "unknown", """{"version":1,"items":[]}"""));
        await Assert.ThrowsAsync<IdentityOperationException>(() => layouts.SaveAsync(
            userId, "control", """{"version":1,"items":[{"cardId":"a","groupId":"main","order":0,"span":1},{"cardId":"a","groupId":"main","order":1,"span":1}]}"""));
        await Assert.ThrowsAsync<IdentityOperationException>(() => layouts.SaveAsync(
            userId, "control", """{"version":1,"items":[{"cardId":"a","groupId":"main","order":0,"span":4}]}"""));
        await Assert.ThrowsAsync<IdentityOperationException>(() => layouts.SaveAsync(
            userId, "control", """{"version":1,"items":[{"cardId":"a","groupId":"main","order":0,"span":1},{"cardId":"b","groupId":"main","order":0,"span":1}]}"""));
    }

    [Fact]
    public async Task ConfigurationSnapshotIncludesSavedLayouts()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Select(user => user.Id).FirstAsync();
        await scope.ServiceProvider.GetRequiredService<UserCardLayoutService>().SaveAsync(
            userId,
            "account",
            """{"version":1,"items":[{"cardId":"account-password","groupId":"account-main","order":0,"span":1}]}""");

        var snapshot = await scope.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().CaptureAsync();
        var layout = Assert.Single(snapshot.Layouts!);
        Assert.Equal(userId, layout.UserId);
        Assert.Equal("account", layout.PageKey);
    }
}
