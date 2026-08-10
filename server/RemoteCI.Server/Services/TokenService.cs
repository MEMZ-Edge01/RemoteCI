using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 进程内 token 服务。token 为随机 GUID，过期时间可配（默认 24 小时）。
/// 注意：服务重启后 token 失效，手表/插件需重新配对；v1 demo 可接受。
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly string _pairCode;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();

    public TokenService(IOptions<ServerOptions> options)
    {
        _pairCode = options.Value.PairCode;
        _ttl = options.Value.TokenTtl;
    }

    public PairResponse Pair(string pairCode, PeerRole role)
    {
        if (!string.Equals(pairCode, _pairCode, StringComparison.Ordinal))
        {
            throw new InvalidPairCodeException();
        }

        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow + _ttl;
        _tokens[token] = new TokenEntry(role, expiresAt);
        return new PairResponse
        {
            Token = token,
            Role = role.ToString().ToLowerInvariant(),
            ExpiresAt = expiresAt,
        };
    }

    public bool TryValidate(string token, out PeerRole role)
    {
        role = default;
        if (string.IsNullOrEmpty(token) ||
            !_tokens.TryGetValue(token, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        role = entry.Role;
        return true;
    }

    private sealed record TokenEntry(PeerRole Role, DateTimeOffset ExpiresAt);
}

/// <summary>配对码错误。</summary>
public sealed class InvalidPairCodeException : Exception;

/// <summary>服务端配置。</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string PairCode { get; set; } = "remoteci-demo";
    public TimeSpan TokenTtl { get; set; } = TimeSpan.FromHours(24);
}
