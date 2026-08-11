using System.Security.Cryptography;
using System.Text;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class AccountMirrorTests
{
    [Fact]
    public void ValidDeviceProofAuthenticatesWithoutPasswordMaterial()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var verifier = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json");
        var mirror = new AccountMirror(path);
        mirror.Apply(CreateSync(userId, sessionId, Convert.ToHexString(verifier).ToLowerInvariant(), DateTimeOffset.UtcNow));
        var challenge = new AuthChallenge
        {
            ChallengeId = Guid.NewGuid().ToString("N"),
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        const string clientNonce = "client-nonce";
        var proof = Convert.ToBase64String(HMACSHA256.HashData(
            verifier,
            Encoding.UTF8.GetBytes(AccountMirror.CanonicalProof(challenge, sessionId, clientNonce))));

        Assert.True(mirror.TryVerify(sessionId, challenge, clientNonce, proof, out var profile));
        Assert.Equal(UserPermissions.All, profile?.Permissions);
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void MirrorOlderThanTwentyFourHoursAuthenticatesAsReadOnly()
    {
        var secret = "device-secret";
        var verifier = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json");
        var mirror = new AccountMirror(path);
        mirror.Apply(CreateSync(userId, sessionId, Convert.ToHexString(verifier).ToLowerInvariant(), DateTimeOffset.UtcNow.AddHours(-25)));
        var challenge = new AuthChallenge
        {
            ChallengeId = "expired-mirror",
            Nonce = "nonce",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        const string clientNonce = "client";
        var proof = Convert.ToBase64String(HMACSHA256.HashData(
            verifier,
            Encoding.UTF8.GetBytes(AccountMirror.CanonicalProof(challenge, sessionId, clientNonce))));

        Assert.True(mirror.TryVerify(sessionId, challenge, clientNonce, proof, out var profile));
        Assert.Equal(UserPermissions.ViewCurrentCourse, profile?.Permissions);
        Assert.False(mirror.AllowsPrivilegedOperations);
    }

    private static AccountSync CreateSync(Guid userId, Guid sessionId, string verifier, DateTimeOffset generatedAt) => new()
    {
        Version = 1,
        GeneratedAt = generatedAt,
        Accounts = [new SyncedAccount
        {
            Id = userId,
            Username = "admin",
            DisplayName = "管理员",
            Role = UserRole.Admin,
            EffectivePermissions = UserPermissions.All,
            Enabled = true,
            Version = 1,
        }],
        Sessions = [new SyncedDeviceSession
        {
            Id = sessionId,
            UserId = userId,
            Verifier = verifier,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        }],
    };
}
