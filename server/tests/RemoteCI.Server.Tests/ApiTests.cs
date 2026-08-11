using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PluginPairingCode_IsOneTimeAndPluginOnly()
    {
        var badRole = await _client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "watch",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

        var first = await _client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "plugin",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var pair = await first.Content.ReadFromJsonAsync<PairResponse>();
        Assert.False(string.IsNullOrWhiteSpace(pair?.Token));

        var replay = await _client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "plugin",
        });
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task PluginPairingCode_RemainsValidPastLegacyExpiryUntilUsed()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "pairing.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pairing = await database.PluginPairingCodes.SingleAsync();
            pairing.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            await database.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "plugin",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_PersistsOnlyIdentityHashAndReturnsRotatableDeviceSession()
    {
        var wrong = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = TestWebApplicationFactory.AdminUsername,
            Password = "definitely-wrong",
            DeviceName = "Test",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var auth = await _factory.LoginAsync();
        Assert.Equal(UserPermissions.All, auth.User.Permissions);
        Assert.Equal(44, auth.AccessToken.Length);
        Assert.False(string.IsNullOrWhiteSpace(auth.DeviceSecret));

        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshSessionRequest
        {
            DeviceSessionId = auth.DeviceSessionId,
            DeviceSecret = auth.DeviceSecret,
        });
        refresh.EnsureSuccessStatusCode();
        var rotated = (await refresh.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(auth.AccessToken, rotated.AccessToken);
        Assert.NotEqual(auth.DeviceSecret, rotated.DeviceSecret);

        await using var database = new FileStream(
            _factory.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        await database.CopyToAsync(buffer);
        var databaseBytes = buffer.ToArray();
        Assert.DoesNotContain(TestWebApplicationFactory.AdminPassword, Encoding.UTF8.GetString(databaseBytes));
    }

    [Fact]
    public async Task Login_OnlyAcceptsId()
    {
        var admin = await _factory.LoginAsync();
        var first = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "login.id",
                DisplayName = "登录用户名",
                Password = "Login-Password-2026",
            }));
        first.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "login.id",
            Password = "Login-Password-2026",
            DeviceName = "ID login",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "登录用户名",
            Password = "Login-Password-2026",
            DeviceName = "Username login",
        })).StatusCode);
    }

    [Fact]
    public async Task PasswordChange_RevokesExistingCloudSessions()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "password.user",
                DisplayName = "改密测试",
                Password = "Original-Password-2026",
            }));
        create.EnsureSuccessStatusCode();
        var user = await LoginAsync("password.user", "Original-Password-2026");
        const string newPassword = "Changed-Password-2026";
        var changed = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/me/password",
            user.AccessToken,
            new ChangePasswordRequest
            {
                CurrentPassword = "Original-Password-2026",
                NewPassword = newPassword,
            }));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var oldToken = await _client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me", user.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        var oldPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "password.user",
            Password = "Original-Password-2026",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        var newLogin = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "password.user",
            Password = newPassword,
        });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task UserCanListAndRevokeOwnDeviceSession()
    {
        var auth = await _factory.LoginAsync();
        var sessionsResponse = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/me/sessions", auth.AccessToken));
        sessionsResponse.EnsureSuccessStatusCode();
        var sessions = (await sessionsResponse.Content.ReadFromJsonAsync<List<DeviceSessionSummary>>())!;
        Assert.Contains(sessions, x => x.Id == auth.DeviceSessionId && x.Current);

        var revoke = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/me/sessions/{auth.DeviceSessionId}", auth.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/me", auth.AccessToken))).StatusCode);
    }

    [Fact]
    public async Task LastAdministrator_CannotBeDeletedDisabledOrDemoted()
    {
        var admin = await _factory.LoginAsync();
        var demote = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{admin.User.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "系统管理员",
                Role = UserRole.User,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        var disable = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{admin.User.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "系统管理员",
                Role = UserRole.Admin,
                Enabled = false,
            }));
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);

        var delete = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/users/{admin.User.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task CustomPermissions_AreEnforcedByEndpointsAndUpdateExistingToken()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "student.permissions",
                DisplayName = "权限测试学生",
                Password = "Student-Password-2026",
                Role = UserRole.User,
            }));
        create.EnsureSuccessStatusCode();
        var student = (await create.Content.ReadFromJsonAsync<UserListItem>())!;
        var login = await LoginAsync("student.permissions", "Student-Password-2026");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/users", login.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/admin/status", login.AccessToken))).StatusCode);

        var update = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{student.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = student.DisplayName,
                Role = UserRole.User,
                Enabled = true,
                GrantedPermissions = UserPermissions.AccessWebUi | UserPermissions.ManageUsers |
                    UserPermissions.SendNotifications | UserPermissions.ManageSchedule,
            }));
        update.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/users", login.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/admin/status", login.AccessToken))).StatusCode);

        var schedule = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/commands",
            login.AccessToken,
            new CommandMessage
            {
                Command = CommandKind.ChangeSchedule,
                ScheduleChange = new ScheduleChangeRequest { Date = "2026-08-11", ExpectedRevision = "x" },
        }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, schedule.StatusCode); // 已授权，但插件离线，不会排队。

        var delete = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/users/{student.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "student.permissions",
            Password = "Student-Password-2026",
        })).StatusCode);
    }

    [Fact]
    public async Task DatabaseRestart_PreservesAccountsAndPermissions()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "restart.db");
        await using (var first = TestWebApplicationFactory.ForDatabase(databasePath))
        {
            var admin = await first.LoginAsync();
            var response = await first.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(
                HttpMethod.Post,
                "/api/users",
                admin.AccessToken,
                new CreateUserRequest
                {
                    Username = "restart.user",
                    DisplayName = "重启用户",
                    Password = "Restart-Password-2026",
                    GrantedPermissions = UserPermissions.AccessWebUi,
                }));
            response.EnsureSuccessStatusCode();
        }

        await using var second = TestWebApplicationFactory.ForDatabase(databasePath);
        var persisted = await second.LoginAsync("restart.user", "Restart-Password-2026");
        Assert.True(persisted.User.Permissions.HasFlag(UserPermissions.AccessWebUi));
    }

    [Fact]
    public async Task RazorLogin_PostWithoutCsrfToken_IsRejected()
    {
        var response = await _client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RazorWebUi_AdminCanOpenEveryConsolePage()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        foreach (var path in new[] { "/", "/Account", "/Users", "/Schedule", "/Notifications" })
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task RazorWebUi_PluginActionsLiveOnOverviewAndRetryReportsResult()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        var usersHtml = await browser.GetStringAsync("/Users");
        Assert.DoesNotContain("生成插件配对码", usersHtml);
        Assert.Contains("<label>ID<input", usersHtml);
        Assert.Contains("<label>用户名<input", usersHtml);
        Assert.Contains("ID：admin", usersHtml);

        var overviewHtml = await browser.GetStringAsync("/");
        Assert.Contains("生成插件配对码", overviewHtml);
        Assert.Contains("重新检测连接", overviewHtml);
        Assert.DoesNotContain("去重试连接</a>", overviewHtml);

        var retry = await PostRazorFormAsync(browser, "/?handler=RetryConnection", overviewHtml);
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal("/", retry.Headers.Location?.OriginalString);
        var retriedHtml = await browser.GetStringAsync("/");
        Assert.Contains("插件仍未连接", WebUtility.HtmlDecode(retriedHtml));

        overviewHtml = await browser.GetStringAsync("/");
        var pairCode = await PostRazorFormAsync(browser, "/?handler=PairCode", overviewHtml);
        Assert.Equal(HttpStatusCode.OK, pairCode.StatusCode);
        var pairCodeHtml = await pairCode.Content.ReadAsStringAsync();
        Assert.Contains("一次性插件配对码", pairCodeHtml);
        Assert.Contains("使用前持续有效", pairCodeHtml);
        Assert.Contains("data-copy-value", pairCodeHtml);
        Assert.Contains("bi-copy", pairCodeHtml);
    }

    [Fact]
    public async Task RazorWebUi_OrdinaryUserOnlyGetsPersonalPages()
    {
        var admin = await _factory.LoginAsync();
        var create = await _client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "web.student",
                DisplayName = "WebUI 普通用户",
                Password = "Web-Student-Password-2026",
            }));
        create.EnsureSuccessStatusCode();

        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, "web.student", "Web-Student-Password-2026");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account")).StatusCode);
        foreach (var path in new[] { "/", "/Users", "/Schedule", "/Notifications" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Denied", response.Headers.Location?.OriginalString);
        }
    }

    private HttpClient CreateBrowserClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static async Task LoginWebUiAsync(HttpClient browser, string username, string password)
    {
        var html = await browser.GetStringAsync("/Login");
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "登录页必须包含 CSRF 令牌");
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRazorFormAsync(
        HttpClient browser, string path, string html)
    {
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Razor 表单页必须包含 CSRF 令牌");
        return await browser.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
    }

    private async Task<AuthResponse> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password,
            DeviceName = "Permission Test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
