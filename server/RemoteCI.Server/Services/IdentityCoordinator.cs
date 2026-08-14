using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 账号、权限和设备会话的唯一入口，所有安全状态变更都在同一数据库事务中完成。
/// </summary>
public sealed partial class IdentityCoordinator(
    AppDbContext db,
    UserManager<AppUser> users,
    IOptions<ServerOptions> options,
    ILogger<IdentityCoordinator> logger)
{
    private readonly ServerOptions _options = options.Value;

    /// <summary>管理员角色的变更串行化：保证“最后管理员”检查与提交之间没有其他管理员变更插入。</summary>
    private static readonly SemaphoreSlim AdminMutationGate = new(1, 1);

    /// <summary>每个账号同时保持的设备会话上限，超出时撤销最早创建的会话。</summary>
    private const int MaxActiveSessionsPerUser = 20;

    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO SystemMetadata (Id, AccountVersion) VALUES (1, 0);", ct);

        // 启动时清理过期超过 30 天的会话行，避免 DeviceSessions 表长期无界增长。
        // SQLite 不支持 DateTimeOffset 比较的 SQL 翻译，先投影再在内存过滤。
        var staleThreshold = DateTimeOffset.UtcNow.AddDays(-30);
        var staleIds = (await db.DeviceSessions.Select(x => new { x.Id, x.ExpiresAt }).ToListAsync(ct))
            .Where(x => x.ExpiresAt < staleThreshold)
            .Select(x => x.Id)
            .ToList();
        foreach (var chunk in staleIds.Chunk(500))
            await db.DeviceSessions.Where(x => chunk.Contains(x.Id)).ExecuteDeleteAsync(ct);

        if (!await users.Users.AnyAsync(ct))
        {
            var configuredPassword = FirstNonEmpty(
                Environment.GetEnvironmentVariable("REMOTECI_ADMIN_PASSWORD"),
                _options.BootstrapAdminPassword);
            var generatedPassword = configuredPassword is null;
            var password = configuredPassword ?? CreateReadableSecret(18);
            ValidatePassword(password);
            var admin = new AppUser
            {
                Id = Guid.NewGuid(),
                UserName = _options.BootstrapAdminUsername,
                DisplayName = "系统管理员",
                Role = UserRole.Admin,
                GrantedPermissions = UserPermissions.None,
                Enabled = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            admin.Version = await NextVersionAsync(ct);
            EnsureIdentitySucceeded(await users.CreateAsync(admin, password));
            if (generatedPassword && _options.LogBootstrapSecrets)
            {
                logger.LogWarning("首次启动已创建管理员 {Username}，一次性初始密码：{Password}。请立即登录并修改密码。",
                    admin.UserName, password);
            }
            else if (generatedPassword)
            {
                logger.LogWarning("首次启动已创建管理员 {Username}，一次性初始密码按配置不写入日志。",
                    admin.UserName);
            }
            else
            {
                logger.LogInformation("首次启动已使用外部配置创建管理员 {Username}。", admin.UserName);
            }
        }

        if (!await db.PluginCredentials.AnyAsync(ct) && !await db.PluginPairingCodes.AnyAsync(ct))
        {
            var configuredCode = FirstNonEmpty(
                Environment.GetEnvironmentVariable("REMOTECI_PLUGIN_PAIR_CODE"),
                _options.BootstrapPluginPairCode);
            var generatedCode = configuredCode is null;
            var code = configuredCode ?? CreateReadableSecret(12);
            await AddPairingCodeAsync(code, ct);
            if (generatedCode && _options.LogBootstrapSecrets)
                logger.LogWarning("首次启动插件一次性配对码：{PairCode}。该码使用后立即失效。", code);
            else if (generatedCode)
                logger.LogWarning("首次启动插件一次性配对码已生成，按配置不写入日志。");
            else
                logger.LogInformation("首次启动已使用外部配置初始化插件一次性配对码。");
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await users.FindByNameAsync(request.Username.Trim());
        if (user is null || !user.Enabled)
        {
            // 恒定时间：对不存在的账号也执行一次同等成本的 PBKDF2 校验，避免响应时间泄露用户名是否存在。
            await users.CheckPasswordAsync(TimingUser(), request.Password);
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "ID 或密码错误");
        }
        if (await users.IsLockedOutAsync(user))
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "失败次数过多，账号已临时锁定，请稍后再试");
        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            // 接入 Identity 锁定：连续失败 8 次锁定 10 分钟（与 WebUI 登录行为一致）。
            await users.AccessFailedAsync(user);
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "ID 或密码错误");
        }
        await users.ResetAccessFailedCountAsync(user);
        return await CreateOrRotateSessionAsync(user, request.DeviceName, null, ct);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshSessionRequest request, CancellationToken ct = default)
    {
        var verifier = Hash(request.DeviceSecret);
        var session = await db.DeviceSessions.Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == request.DeviceSessionId, ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= DateTimeOffset.UtcNow ||
            !session.User.Enabled || !FixedEquals(session.VerifierHash, verifier))
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "设备会话已失效");

        return await CreateOrRotateSessionAsync(session.User, session.DeviceName, session, ct);
    }

    public async Task<AuthPrincipal?> ValidateAccessTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        var session = await db.DeviceSessions.Include(x => x.User)
            .SingleOrDefaultAsync(x => x.AccessTokenHash == hash, ct);
        if (session is null || session.RevokedAt is not null || session.AccessExpiresAt <= DateTimeOffset.UtcNow ||
            session.ExpiresAt <= DateTimeOffset.UtcNow || !session.User.Enabled)
            return null;

        // WS 高频消息每条都写 LastSeenAt 会造成 SQLite 写放大与锁竞争；最多每分钟落库一次。
        var now = DateTimeOffset.UtcNow;
        if (now - session.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }
        return new AuthPrincipal(PeerRole.Watch, ToProfile(session.User), session.Id);
    }

    public async Task<AuthPrincipal?> ValidatePluginTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        var credential = await db.PluginCredentials.SingleOrDefaultAsync(x => x.TokenHash == hash && x.Enabled, ct);
        if (credential is null) return null;
        var now = DateTimeOffset.UtcNow;
        if (now - credential.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            credential.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }
        return new AuthPrincipal(PeerRole.Plugin, null, null);
    }

    public async Task<AuthPrincipal?> ValidateAnyTokenAsync(string token, CancellationToken ct = default) =>
        await ValidatePluginTokenAsync(token, ct) ?? await ValidateAccessTokenAsync(token, ct);

    public async Task<IReadOnlyList<PluginCredentialInfo>> ListPluginCredentialsAsync(CancellationToken ct = default)
    {
        // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；凭证数量极少，取回后在内存排序。
        var credentials = await db.PluginCredentials.AsNoTracking().ToListAsync(ct);
        return credentials
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new PluginCredentialInfo
            {
                Id = x.Id,
                Name = x.Name,
                CreatedAt = x.CreatedAt,
                LastSeenAt = x.LastSeenAt,
                Enabled = x.Enabled,
            }).ToList();
    }

    /// <summary>吊销插件长期凭据；其在线 WebSocket 会在下一条消息的令牌校验时被断开。</summary>
    public async Task RevokePluginCredentialAsync(Guid id, CancellationToken ct = default)
    {
        var credential = await db.PluginCredentials.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "插件凭证不存在");
        if (!credential.Enabled) return; // 幂等。
        credential.Enabled = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task<PairResponse> PairPluginAsync(PairRequest request, CancellationToken ct = default)
    {
        var hash = Hash(request.PairCode);
        var now = DateTimeOffset.UtcNow;
        // 原子消费配对码：并发请求中只有一个能把 UsedAt 从未置位更新为当前时间，
        // 防止两个请求同时读到“未使用”的配对码并各自签发插件凭证。
        var consumed = await db.PluginPairingCodes
            .Where(x => x.CodeHash == hash && x.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedAt, now), ct);
        if (consumed == 0)
            throw new IdentityOperationException(ApiErrorCodes.PairCodeInvalid, "插件配对码无效或已使用");

        var token = CreateSecret(32);
        db.PluginCredentials.Add(new PluginCredential
        {
            Id = Guid.NewGuid(),
            Name = "ClassIsland 插件",
            TokenHash = Hash(token),
            CreatedAt = now,
            LastSeenAt = now,
            Enabled = true,
        });
        await db.SaveChangesAsync(ct);
        return new PairResponse { Token = token, Role = "plugin", ExpiresAt = null };
    }

    public async Task<string> CreatePluginPairingCodeAsync(CancellationToken ct = default)
    {
        var code = CreateReadableSecret(12);
        await AddPairingCodeAsync(code, ct);
        return code;
    }

    public async Task<UserProfile?> GetProfileAsync(Guid id, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id.ToString());
        return user is null || !user.Enabled ? null : ToProfile(user);
    }

    public async Task<IReadOnlyList<UserListItem>> ListUsersAsync(CancellationToken ct = default) =>
        await users.Users.OrderByDescending(x => x.Role).ThenBy(x => x.UserName)
            .Select(x => new UserListItem
            {
                Id = x.Id,
                Username = x.UserName!,
                DisplayName = x.DisplayName,
                Role = x.Role,
                GrantedPermissions = x.GrantedPermissions,
                EffectivePermissions = x.Role == UserRole.Admin
                    ? UserPermissions.All
                    : UserPermissions.ViewCurrentCourse | x.GrantedPermissions,
                Enabled = x.Enabled,
                UpdatedAt = x.UpdatedAt,
            }).ToListAsync(ct);

    public async Task<UserListItem> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        ValidateUserInput(request.Username, request.DisplayName, request.Password, request.Role);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Role = request.Role,
            GrantedPermissions = NormalizeGrants(request.Role, request.GrantedPermissions),
            Enabled = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.CreateAsync(user, request.Password));
        return ToListItem(user);
    }

    public async Task<UserListItem> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        ValidateDisplayName(request.DisplayName);
        ValidateRole(request.Role);
        await AdminMutationGate.WaitAsync(ct);
        try
        {
            var user = await RequireUserAsync(id);
            if (user.Role == UserRole.Admin && user.Enabled && (request.Role != UserRole.Admin || !request.Enabled))
                await GuardLastAdminAsync(user.Id, ct);

            var mustRevoke = user.Enabled && !request.Enabled;
            user.DisplayName = request.DisplayName.Trim();
            user.Role = request.Role;
            user.GrantedPermissions = NormalizeGrants(request.Role, request.GrantedPermissions);
            user.Enabled = request.Enabled;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.Version = await NextVersionAsync(ct);
            EnsureIdentitySucceeded(await users.UpdateAsync(user));
            if (mustRevoke) await RevokeAllSessionsAsync(user.Id, ct);
            return ToListItem(user);
        }
        finally
        {
            AdminMutationGate.Release();
        }
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        await AdminMutationGate.WaitAsync(ct);
        try
        {
            var user = await RequireUserAsync(id);
            if (user.Role == UserRole.Admin && user.Enabled) await GuardLastAdminAsync(user.Id, ct);
            EnsureIdentitySucceeded(await users.DeleteAsync(user));
            await NextVersionAsync(ct);
        }
        finally
        {
            AdminMutationGate.Release();
        }
    }

    public async Task ResetPasswordAsync(Guid id, string password, CancellationToken ct = default)
    {
        ValidatePassword(password);
        var user = await RequireUserAsync(id);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        EnsureIdentitySucceeded(await users.ResetPasswordAsync(user, token, password));
        await users.UpdateSecurityStampAsync(user);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.UpdateAsync(user));
        await RevokeAllSessionsAsync(id, ct);
    }

    public async Task ChangePasswordAsync(Guid id, ChangePasswordRequest request, CancellationToken ct = default)
    {
        ValidatePassword(request.NewPassword);
        var user = await RequireUserAsync(id);
        EnsureIdentitySucceeded(await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword));
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.UpdateAsync(user));
        await RevokeAllSessionsAsync(id, ct);
    }

    public async Task<IReadOnlyList<DeviceSessionSummary>> ListSessionsAsync(Guid userId, Guid? currentId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.DeviceSessions.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(ct);
        return sessions.Where(x => x.ExpiresAt > now).OrderByDescending(x => x.LastSeenAt)
            .Select(x => new DeviceSessionSummary
            {
                Id = x.Id,
                DeviceName = x.DeviceName,
                CreatedAt = x.CreatedAt,
                LastSeenAt = x.LastSeenAt,
                ExpiresAt = x.ExpiresAt,
                Current = currentId == x.Id,
            }).ToList();
    }

    public async Task RevokeSessionAsync(Guid ownerId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.DeviceSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == ownerId, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "设备会话不存在");
        session.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await NextVersionAsync(ct);
    }

    public async Task<AccountSync> CreateSyncAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var accountVersion = await db.SystemMetadata.Where(x => x.Id == 1).Select(x => x.AccountVersion).SingleAsync(ct);
        var accounts = await users.Users.Select(x => new SyncedAccount
        {
            Id = x.Id,
            Username = x.UserName!,
            DisplayName = x.DisplayName,
            Role = x.Role,
            GrantedPermissions = x.GrantedPermissions,
            EffectivePermissions = x.Role == UserRole.Admin
                ? UserPermissions.All
                : UserPermissions.ViewCurrentCourse | x.GrantedPermissions,
            Enabled = x.Enabled,
            Version = x.Version,
        }).ToListAsync(ct);
        var activeSessions = await db.DeviceSessions.Include(x => x.User)
            .Where(x => x.RevokedAt == null).ToListAsync(ct);
        var sessions = activeSessions.Where(x => x.ExpiresAt > now && x.User.Enabled)
            .Select(x => new SyncedDeviceSession
            {
                Id = x.Id,
                UserId = x.UserId,
                Verifier = x.VerifierHash,
                ExpiresAt = x.ExpiresAt,
            }).ToList();
        return new AccountSync
        {
            Version = accountVersion,
            ServerVersion = AppVersion.Version,
            GeneratedAt = now,
            Accounts = accounts,
            Sessions = sessions,
        };
    }

    private async Task<AuthResponse> CreateOrRotateSessionAsync(
        AppUser user, string deviceName, DeviceSession? existing, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var accessToken = CreateSecret(32);
        var deviceSecret = CreateSecret(32);
        var session = existing ?? new DeviceSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceName = NormalizeDeviceName(deviceName),
            CreatedAt = now,
        };
        session.AccessTokenHash = Hash(accessToken);
        session.VerifierHash = Hash(deviceSecret);
        session.AccessExpiresAt = now + _options.AccessTokenTtl;
        session.ExpiresAt = now + _options.DeviceSessionTtl;
        session.LastSeenAt = now;
        session.RevokedAt = null;
        if (existing is null)
        {
            db.DeviceSessions.Add(session);
            // 每账号活跃会话上限：超出时撤销最早创建的一批，防止长周期累积。
            // SQLite 不支持 DateTimeOffset 比较的 SQL 翻译，先取未撤销会话再在内存中过滤。
            var candidates = await db.DeviceSessions
                .Where(x => x.UserId == user.Id && x.RevokedAt == null)
                .ToListAsync(ct);
            var overflow = candidates
                .Where(x => x.ExpiresAt > now)
                .OrderByDescending(x => x.CreatedAt)
                .Skip(MaxActiveSessionsPerUser - 1)
                .ToList();
            foreach (var old in overflow) old.RevokedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await NextVersionAsync(ct);
        return new AuthResponse
        {
            AccessToken = accessToken,
            AccessExpiresAt = session.AccessExpiresAt,
            DeviceSessionId = session.Id,
            DeviceSecret = deviceSecret,
            DeviceExpiresAt = session.ExpiresAt,
            User = ToProfile(user),
        };
    }

    private async Task AddPairingCodeAsync(string code, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        db.PluginPairingCodes.Add(new PluginPairingCode
        {
            Id = Guid.NewGuid(),
            CodeHash = Hash(code),
            CreatedAt = now,
            // 保留旧数据库的非空列结构；配对码的实际生命周期只由 UsedAt 控制。
            ExpiresAt = DateTimeOffset.MaxValue,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<AppUser> RequireUserAsync(Guid id) => await users.FindByIdAsync(id.ToString())
        ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "用户不存在");

    private async Task GuardLastAdminAsync(Guid id, CancellationToken ct)
    {
        var otherAdmins = await users.Users.CountAsync(x => x.Id != id && x.Enabled && x.Role == UserRole.Admin, ct);
        if (otherAdmins == 0) throw new IdentityOperationException("LAST_ADMIN", "不能删除、禁用或降级最后一个管理员");
    }

    private async Task RevokeAllSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.DeviceSessions.Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), ct);
    }

    /// <summary>读取全局“强制在标题显示发送人”设置，默认开启以保持既有行为。</summary>
    public async Task<bool> GetForceSenderInTitleAsync(CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        return metadata.ForceSenderInTitle;
    }

    /// <summary>更新全局“强制在标题显示发送人”设置，返回可广播给在线手表的快照。</summary>
    public async Task<SettingsSync> SetForceSenderInTitleAsync(bool force, CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.ForceSenderInTitle = force;
        await db.SaveChangesAsync(ct);
        return new SettingsSync { ForceSenderInTitle = force };
    }

    private async Task<long> NextVersionAsync(CancellationToken ct)
    {
        // 事务内原子自增：并发变更各自拿到不同的新版本号，避免读到旧值后各自写同一个 +1 导致丢递增。
        await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE SystemMetadata SET AccountVersion = AccountVersion + 1 WHERE Id = 1;", ct);
            var version = await db.SystemMetadata.Where(x => x.Id == 1)
                .Select(x => x.AccountVersion)
                .SingleAsync(ct);
            await db.Database.CommitTransactionAsync(ct);
            return version;
        }
        catch
        {
            await db.Database.RollbackTransactionAsync(ct);
            throw;
        }
    }

    private static UserProfile ToProfile(AppUser user) => new()
    {
        Id = user.Id,
        Username = user.UserName!,
        DisplayName = user.DisplayName,
        Role = user.Role,
        GrantedPermissions = user.GrantedPermissions,
        Permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions),
        Version = user.Version,
    };

    private static UserListItem ToListItem(AppUser user) => new()
    {
        Id = user.Id,
        Username = user.UserName!,
        DisplayName = user.DisplayName,
        Role = user.Role,
        GrantedPermissions = user.GrantedPermissions,
        EffectivePermissions = RolePermissions.Effective(user.Role, user.GrantedPermissions),
        Enabled = user.Enabled,
        UpdatedAt = user.UpdatedAt,
    };

    private static UserPermissions NormalizeGrants(UserRole role, UserPermissions grants) => role == UserRole.Admin
        ? UserPermissions.None
        : grants & (UserPermissions.AccessWebUi | UserPermissions.ManageUsers |
            UserPermissions.SendNotifications | UserPermissions.ManageSchedule | UserPermissions.SystemControl);

    private static string NormalizeDeviceName(string value) => string.IsNullOrWhiteSpace(value)
        ? "Wear OS"
        : value.Trim()[..Math.Min(value.Trim().Length, 80)];

    private static string CreateSecret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
    private static string CreateReadableSecret(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private AppUser? _timingUser;

    /// <summary>恒定时间校验用的占位用户；首次校验不存在账号时用真实哈希器生成等成本哈希。</summary>
    private AppUser TimingUser() => _timingUser ??= new AppUser
    {
        UserName = "remoteci-timing-equalizer",
        PasswordHash = users.PasswordHasher.HashPassword(new AppUser(), CreateSecret(16)),
    };

    private static void ValidateUserInput(string username, string displayName, string password, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(username) || !UsernameRegex().IsMatch(username.Trim()))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "ID 需为 3-32 位字母、数字、点、下划线或短横线");
        ValidateDisplayName(displayName);
        ValidatePassword(password);
        ValidateRole(role);
    }

    private static void ValidateDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "用户名需为 1-40 个字符");
    }

    private static void ValidatePassword(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 8 or > 128)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "密码需为 8-128 个字符");
    }

    private static void ValidateRole(UserRole role)
    {
        if (role is not UserRole.User and not UserRole.Admin)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "角色无效");
    }

    private static void EnsureIdentitySucceeded(IdentityResult result)
    {
        if (result.Succeeded) return;
        var duplicate = result.Errors.FirstOrDefault(x => x.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        var message = string.Join("；", result.Errors.Select(x => x.Description));
        throw new IdentityOperationException(duplicate is null ? ApiErrorCodes.InvalidRequest : "USERNAME_EXISTS", message);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();
}

public sealed record AuthPrincipal(PeerRole PeerRole, UserProfile? User, Guid? DeviceSessionId)
{
    public bool IsPlugin => PeerRole == PeerRole.Plugin;
    public bool IsAdmin => User?.Role == UserRole.Admin;
}

public sealed class IdentityOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
