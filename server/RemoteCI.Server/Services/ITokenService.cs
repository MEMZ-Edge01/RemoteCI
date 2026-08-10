using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 配对/token 服务：配对码换取访问 token，后续 REST/WebSocket 均凭 token 认证。
/// demo 版为进程内内存实现，后续可替换为持久化/签名方案而不改调用方。
/// </summary>
public interface ITokenService
{
    /// <summary>校验配对码并发放 token。</summary>
    PairResponse Pair(string pairCode, PeerRole role);

    /// <summary>校验 token，成功返回角色。</summary>
    bool TryValidate(string token, out PeerRole role);
}
