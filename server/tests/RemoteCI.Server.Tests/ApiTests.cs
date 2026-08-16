using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
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
    public async Task PluginPairingCode_ExpiredCodeIsRejected()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "pairing.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pairing = await database.PluginPairingCodes.SingleAsync();
            pairing.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/plugin/pair", new PairRequest
        {
            PairCode = TestWebApplicationFactory.TestPairCode,
            Role = "plugin",
        });

        // 启用 ExpiresAt 后，过期配对码不能再签发插件凭证。
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PluginPairingCode_WebUiGeneratedCodeExpiresInThirtyMinutes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "pairing-ttl.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var auth = await factory.LoginAsync();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/plugin/pairing-code", auth.AccessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // SQLite 不支持 DateTimeOffset 比较的 SQL 翻译；引导码不限时（MaxValue），只筛限时的新码。
        var created = (await database.PluginPairingCodes.ToListAsync())
            .Single(x => x.ExpiresAt < DateTimeOffset.MaxValue);
        var remaining = created.ExpiresAt - DateTimeOffset.UtcNow;
        Assert.InRange(remaining, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
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

    [Theory]
    [InlineData("", "正文")]
    [InlineData("标题", "")]
    [InlineData("", "")]
    public async Task RazorWebUi_NotificationAllowsEmptyTitleOrMessage(string title, string message)
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var notificationHtml = await browser.GetStringAsync("/Notifications");

        var response = await PostRazorFormAsync(
            browser,
            "/Notifications",
            notificationHtml,
            new Dictionary<string, string>
            {
                ["Input.Title"] = title,
                ["Input.Message"] = message,
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task RazorWebUi_SchedulePageExposesManualPullAndIntervalOptions()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var scheduleHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

        Assert.Contains("立即向插件拉取课表", scheduleHtml);
        Assert.Contains("强制覆盖服务端缓存", scheduleHtml);
        Assert.Contains("data-schedule-pull-progress", scheduleHtml);
        Assert.Contains("每 15 分钟", scheduleHtml);
        Assert.Contains("每小时", scheduleHtml);
        Assert.Contains("每 6 小时", scheduleHtml);
        Assert.Contains("每天", scheduleHtml);

        var response = await PostRazorFormAsync(
            browser,
            "/Schedule?handler=PullInterval",
            scheduleHtml,
            new Dictionary<string, string> { ["PullInterval"] = "60" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SchedulePullSettings>();
        Assert.Equal(SchedulePullInterval.Hourly, await settings.GetIntervalAsync());
    }

    [Fact]
    public async Task RazorWebUi_HidesInactiveSchedulePullProgress()
    {
        using var browser = CreateBrowserClient();
        var css = await browser.GetStringAsync("/app.css");

        Assert.Contains(".schedule-pull-progress[hidden]", css);
    }

    [Fact]
    public async Task RazorWebUi_ShowsGlobalScheduleTaskAndDisablesDuplicatePull()
    {
        using var scope = _factory.Services.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<ScheduleSyncTaskTracker>();
        var running = tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.Automatic));
        try
        {
            using var browser = CreateBrowserClient();
            await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
            var scheduleHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/Schedule"));

            Assert.Contains("已有课表任务正在执行", scheduleHtml);
            Assert.Contains("请勿重复操作", scheduleHtml);
            Assert.Contains("disabled", scheduleHtml);
        }
        finally
        {
            tracker.Observe(new ScheduleSyncStatus
            {
                TaskId = running.TaskId,
                Source = running.Source,
                State = ScheduleSyncTaskState.Failed,
                Message = "测试清理",
                StartedAt = running.StartedAt,
                FinishedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    [Fact]
    public async Task RazorWebUi_CheckUpdateDoesNotValidateUntouchedPasswordForm()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var accountHtml = await browser.GetStringAsync("/Account");
        var previousRuntime = Environment.GetEnvironmentVariable("REMOTECI_RUNTIME");

        try
        {
            // 让处理器走无需访问 GitHub 的 fnOS 分支，只验证真实 Razor 绑定与 ModelState 行为。
            Environment.SetEnvironmentVariable("REMOTECI_RUNTIME", "fnos");
            var response = await PostRazorFormAsync(browser, "/Account?handler=CheckUpdate", accountHtml);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("由fnOS应用商店管理", responseHtml);
            Assert.DoesNotContain("<li>The CurrentPassword field is required.</li>", responseHtml);
            Assert.DoesNotContain("<li>The NewPassword field is required.</li>", responseHtml);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REMOTECI_RUNTIME", previousRuntime);
        }
    }

    [Fact]
    public async Task RazorWebUi_DevelopmentBuildDisablesSelfUpdate()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        var response = await browser.GetAsync("/Account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(UpdateService.DevelopmentManagedMessage, html);
        Assert.DoesNotContain("强制更新（同版本重新下载并覆盖安装）", html);
    }

    [Fact]
    public async Task RazorWebUi_PasswordHandlerStillValidatesEmptyPasswordFields()
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var accountHtml = await browser.GetStringAsync("/Account");

        var response = await PostRazorFormAsync(browser, "/Account?handler=Password", accountHtml);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("<li>The CurrentPassword field is required.</li>", responseHtml);
        Assert.Contains("<li>The NewPassword field is required.</li>", responseHtml);
    }

    [Theory]
    [InlineData("非法 ID", "Valid-Password-2026", "Create.Username")]
    [InlineData("admin", "Valid-Password-2026", "Create.Username")]
    [InlineData("valid.id", "short", "Create.Password")]
    public async Task RazorWebUi_CreateUserClearsOnlyTheInvalidField(
        string username,
        string password,
        string expectedInvalidField)
    {
        using var browser = CreateBrowserClient();
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);
        var usersHtml = await browser.GetStringAsync("/Users");
        var response = await PostRazorFormAsync(
            browser,
            "/Users?handler=Create",
            usersHtml,
            new Dictionary<string, string>
            {
                ["Create.Username"] = username,
                ["Create.DisplayName"] = "应保留的用户名",
                ["Create.Password"] = password,
                ["Create.Role"] = ((int)UserRole.Admin).ToString(),
                ["Create.AccessWebUi"] = "true",
                ["Create.ManageSchedule"] = "true",
            },
            ajax: true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var invalidFields = payload.RootElement.GetProperty("invalidFields").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        Assert.True(
            invalidFields.SequenceEqual([expectedInvalidField]),
            $"无效字段不匹配：{payload.RootElement}");
        Assert.False(payload.RootElement.TryGetProperty("password", out _));
        Assert.False(payload.RootElement.TryGetProperty("displayName", out _));
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

    [Fact]
    public async Task Login_LocksOutAfterRepeatedFailuresButLeavesOtherAccountsAlone()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "lockout.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "lockout.user",
                DisplayName = "锁定测试",
                Password = "Lockout-Password-2026",
            }));
        create.EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                Username = "lockout.user",
                Password = "wrong-password",
                DeviceName = "Lockout Test",
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // 第 9 次即使密码正确也处于锁定状态（与 WebUI 登录行为一致）。
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = "lockout.user",
            Password = "Lockout-Password-2026",
            DeviceName = "Lockout Test",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);

        // 其他账号不受影响。
        var other = await factory.LoginAsync();
        Assert.True(other.AccessToken.Length > 0);
    }

    [Fact]
    public async Task PluginPairingCode_ConcurrentConsumptionOnlySucceedsOnce()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "pair-race.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);

        var requests = Enumerable.Range(0, 8).Select(_ => factory.CreateClient().PostAsJsonAsync(
            "/api/plugin/pair",
            new PairRequest { PairCode = TestWebApplicationFactory.TestPairCode, Role = "plugin" }));
        var responses = await Task.WhenAll(requests);

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task ManageUsersGrant_CannotCreateEditOrResetAdmins()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "escalation.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "manager.user",
                DisplayName = "普通管理者",
                Password = "Manager-Password-2026",
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        create.EnsureSuccessStatusCode();
        var manager = await LoginViaAsync(client, "manager.user", "Manager-Password-2026");

        // 不能创建管理员账号。
        var createAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            manager.AccessToken,
            new CreateUserRequest
            {
                Username = "sneaky.admin",
                DisplayName = "伪装管理员",
                Password = "Sneaky-Password-2026",
                Role = UserRole.Admin,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, createAdmin.StatusCode);

        // 不能把自己或他人升级为管理员。
        var promote = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{manager.User.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "普通管理者",
                Role = UserRole.Admin,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);

        // 不能编辑、重置密码或删除管理员账号。
        var editAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{admin.User.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "系统管理员",
                Role = UserRole.Admin,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, editAdmin.StatusCode);
        var resetAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            $"/api/users/{admin.User.Id}/password",
            manager.AccessToken,
            new ResetPasswordRequest { Password = "Stolen-Password-2026" }));
        Assert.Equal(HttpStatusCode.Forbidden, resetAdmin.StatusCode);
        var deleteAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete,
            $"/api/users/{admin.User.Id}",
            manager.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, deleteAdmin.StatusCode);

        // 管理普通账号仍然可行（权限没有被误伤）。
        var editPeer = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{manager.User.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "普通管理者·改",
                Role = UserRole.User,
                Enabled = true,
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        Assert.Equal(HttpStatusCode.OK, editPeer.StatusCode);
    }

    [Fact]
    public async Task ManageUsersGrant_CannotTakeOverDisabledAdmins()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "disabled-admin.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();

        // 第二个管理员被禁用后，守卫不得因 GetProfile 返回 null 而放行普通用户接管。
        var createAdmin = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "second.admin",
                DisplayName = "被禁用的管理员",
                Password = "Second-Admin-Password-2026",
                Role = UserRole.Admin,
            }));
        createAdmin.EnsureSuccessStatusCode();
        var secondAdmin = (await createAdmin.Content.ReadFromJsonAsync<UserListItem>())!;
        var disable = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{secondAdmin.Id}",
            admin.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "被禁用的管理员",
                Role = UserRole.Admin,
                Enabled = false,
            }));
        disable.EnsureSuccessStatusCode();

        var createManager = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "manager.user",
                DisplayName = "普通管理者",
                Password = "Manager-Password-2026",
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        createManager.EnsureSuccessStatusCode();
        var manager = await LoginViaAsync(client, "manager.user", "Manager-Password-2026");

        // 不能通过重新启用被禁用的管理员完成接管。
        var reenable = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put,
            $"/api/users/{secondAdmin.Id}",
            manager.AccessToken,
            new UpdateUserRequest
            {
                DisplayName = "被禁用的管理员",
                Role = UserRole.User,
                Enabled = true,
            }));
        Assert.Equal(HttpStatusCode.Forbidden, reenable.StatusCode);
        var resetDisabled = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            $"/api/users/{secondAdmin.Id}/password",
            manager.AccessToken,
            new ResetPasswordRequest { Password = "Stolen-Password-2026" }));
        Assert.Equal(HttpStatusCode.Forbidden, resetDisabled.StatusCode);
        var deleteDisabled = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete,
            $"/api/users/{secondAdmin.Id}",
            manager.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, deleteDisabled.StatusCode);
    }

    [Fact]
    public async Task PluginCredentials_ListedAndRevokedByAdminOnly_AndRevokedTokenStopsConnecting()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "credential.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        var pluginToken = await factory.GetPluginTokenAsync();
        var admin = await factory.LoginAsync();

        var list = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/plugins/credentials", admin.AccessToken));
        list.EnsureSuccessStatusCode();
        var credentials = (await list.Content.ReadFromJsonAsync<List<PluginCredentialInfo>>())!;
        var credential = Assert.Single(credentials);
        Assert.True(credential.Enabled);

        // 未登录与仅持有 ManageUsers 的普通用户均不可访问。
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/plugins/credentials")).StatusCode);
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "credential.manager",
                DisplayName = "凭证管理者",
                Password = "Manager-Password-2026",
                GrantedPermissions = UserPermissions.ManageUsers,
            }));
        create.EnsureSuccessStatusCode();
        var manager = await LoginViaAsync(client, "credential.manager", "Manager-Password-2026");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/plugins/credentials", manager.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/plugins/credentials/{credential.Id}", manager.AccessToken))).StatusCode);

        // 管理员吊销后：列表标记禁用，被吊销令牌无法再建立 WebSocket。
        var revoke = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, $"/api/plugins/credentials/{credential.Id}", admin.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var after = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, "/api/plugins/credentials", admin.AccessToken));
        var afterList = (await after.Content.ReadFromJsonAsync<List<PluginCredentialInfo>>())!;
        Assert.False(afterList.Single(x => x.Id == credential.Id).Enabled);

        var webSocket = factory.Server.CreateWebSocketClient();
        // TestServer 握手失败抛 InvalidOperationException 而非 WebSocketException，按状态码断言。
        var connectError = await Assert.ThrowsAnyAsync<Exception>(() => webSocket.ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(pluginToken)}"),
            CancellationToken.None));
        Assert.Contains("401", connectError.Message);
    }

    [Fact]
    public async Task RazorWebUi_PluginCredentialsVisibleToAdminAndRevocable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "credential-ui.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath);
        var client = factory.CreateClient();
        await factory.GetPluginTokenAsync(); // 制造一条插件凭证（名称“ClassIsland 插件”）。

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(browser, TestWebApplicationFactory.AdminUsername, TestWebApplicationFactory.AdminPassword);

        var overview = await browser.GetStringAsync("/");
        Assert.Contains("插件凭据", WebUtility.HtmlDecode(overview));
        Assert.Contains("ClassIsland 插件", WebUtility.HtmlDecode(overview));
        Assert.Contains("最近活跃", WebUtility.HtmlDecode(overview));

        Guid credentialId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            credentialId = (await database.PluginCredentials.SingleAsync()).Id;
        }
        var revoke = await PostRazorFormAsync(browser, $"/?handler=RevokeCredential&id={credentialId}", overview);
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);

        var afterHtml = WebUtility.HtmlDecode(await browser.GetStringAsync("/"));
        Assert.Contains("已吊销", afterHtml);

        // 普通用户看不到该节。
        var admin = await factory.LoginAsync();
        var create = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post,
            "/api/users",
            admin.AccessToken,
            new CreateUserRequest
            {
                Username = "view.only",
                DisplayName = "只读用户",
                Password = "View-Only-Password-2026",
                GrantedPermissions = UserPermissions.AccessWebUi,
            }));
        create.EnsureSuccessStatusCode();
        using var userBrowser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        await LoginWebUiAsync(userBrowser, "view.only", "View-Only-Password-2026");
        var userHtml = WebUtility.HtmlDecode(await userBrowser.GetStringAsync("/"));
        Assert.DoesNotContain("插件凭据", userHtml);
    }

    [Fact]
    public async Task BootstrapWithSecretLoggingDisabled_StillBootsAndBindsOption()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "quiet-bootstrap.db");
        await using var factory = TestWebApplicationFactory.ForDatabase(databasePath, new Dictionary<string, string?>
        {
            ["Server:LogBootstrapSecrets"] = "false",
        });

        var options = factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        Assert.False(options.LogBootstrapSecrets);

        // 关闭日志输出后初始管理员照常创建、可登录（行为不受开关影响）。
        var admin = await factory.LoginAsync();
        Assert.True(admin.AccessToken.Length > 0);
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
        HttpClient browser,
        string path,
        string html,
        IReadOnlyDictionary<string, string>? values = null,
        bool ajax = false)
    {
        var match = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Razor 表单页必须包含 CSRF 令牌");
        var fields = new Dictionary<string, string>(values ?? new Dictionary<string, string>())
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        if (ajax)
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Accept.ParseAdd("application/json");
        }
        return await browser.SendAsync(request);
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

    private static async Task<AuthResponse> LoginViaAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password,
            DeviceName = "Isolated Factory Test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
