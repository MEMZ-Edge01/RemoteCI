using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 服务端授权镜像。只保存账号元数据和设备会话验证器，绝不接收或保存用户密码哈希。
/// </summary>
public sealed class AccountMirror
{
    public static readonly TimeSpan PrivilegedOfflineTtl = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private readonly string _path;
    private AccountSync _sync;

    public AccountMirror(string path)
    {
        _path = path;
        _sync = Load(path);
        // v1 草稿曾写入密码哈希；启动时立即按 v2 DTO 原子重写，移除所有未知旧字段。
        if (File.Exists(path)) PersistLocked();
    }

    public long Version { get { lock (_gate) return _sync.Version; } }
    public string ServerVersion { get { lock (_gate) return _sync.ServerVersion; } }
    public DateTimeOffset GeneratedAt { get { lock (_gate) return _sync.GeneratedAt; } }
    public bool AllowsPrivilegedOperations
    {
        get { lock (_gate) return DateTimeOffset.UtcNow - _sync.GeneratedAt <= PrivilegedOfflineTtl; }
    }

    public bool TryVerify(
        Guid sessionId,
        AuthChallenge challenge,
        string clientNonce,
        string proof,
        out UserProfile? profile)
    {
        lock (_gate)
        {
            profile = null;
            var now = DateTimeOffset.UtcNow;
            var session = _sync.Sessions.FirstOrDefault(x => x.Id == sessionId && x.ExpiresAt > now);
            if (session is null) return false;
            var account = _sync.Accounts.FirstOrDefault(x => x.Id == session.UserId && x.Enabled);
            if (account is null) return false;

            byte[] verifier;
            byte[] supplied;
            try
            {
                verifier = Convert.FromHexString(session.Verifier);
                supplied = Convert.FromBase64String(proof);
            }
            catch (FormatException)
            {
                return false;
            }

            var canonical = CanonicalProof(challenge, sessionId, clientNonce);
            var expected = HMACSHA256.HashData(verifier, Encoding.UTF8.GetBytes(canonical));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return false;

            profile = account.ToProfile();
            if (now - _sync.GeneratedAt > PrivilegedOfflineTtl)
                profile.Permissions = UserPermissions.ViewCurrentCourse;
            return true;
        }
    }

    public UserProfile? GetProfile(Guid id)
    {
        lock (_gate)
        {
            var account = _sync.Accounts.FirstOrDefault(x => x.Enabled && x.Id == id);
            if (account is null) return null;
            var profile = account.ToProfile();
            if (DateTimeOffset.UtcNow - _sync.GeneratedAt > PrivilegedOfflineTtl)
                profile.Permissions = UserPermissions.ViewCurrentCourse;
            return profile;
        }
    }

    public UserProfile? GetProfileForSession(Guid userId, Guid sessionId)
    {
        lock (_gate)
        {
            if (!_sync.Sessions.Any(x => x.Id == sessionId && x.UserId == userId && x.ExpiresAt > DateTimeOffset.UtcNow))
                return null;
            var account = _sync.Accounts.FirstOrDefault(x => x.Enabled && x.Id == userId);
            if (account is null) return null;
            var profile = account.ToProfile();
            if (DateTimeOffset.UtcNow - _sync.GeneratedAt > PrivilegedOfflineTtl)
                profile.Permissions = UserPermissions.ViewCurrentCourse;
            return profile;
        }
    }

    public void Apply(AccountSync sync)
    {
        lock (_gate)
        {
            if (sync.Version < _sync.Version) return;
            _sync = sync;
            PersistLocked();
        }
        Changed?.Invoke();
    }

    public event Action? Changed;

    public static string CanonicalProof(AuthChallenge challenge, Guid sessionId, string clientNonce) =>
        $"{Protocol.Version}|{challenge.ChallengeId}|{challenge.Nonce}|{clientNonce}|{sessionId:N}";

    private void PersistLocked()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_sync, JsonDefaults.Options));
        File.Move(temporary, _path, true);
    }

    private static AccountSync Load(string path)
    {
        if (!File.Exists(path)) return new AccountSync();
        try
        {
            return JsonSerializer.Deserialize<AccountSync>(File.ReadAllText(path), JsonDefaults.Options)
                ?? new AccountSync();
        }
        catch (JsonException)
        {
            return new AccountSync();
        }
    }
}
