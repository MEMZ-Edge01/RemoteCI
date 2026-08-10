using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>配对请求体。</summary>
public sealed class PairRequest
{
    [JsonPropertyName("pairCode")]
    public required string PairCode { get; set; }

    /// <summary>申请角色：plugin 或 watch。</summary>
    [JsonPropertyName("role")]
    public required string Role { get; set; }
}

/// <summary>配对成功响应。</summary>
public sealed class PairResponse
{
    [JsonPropertyName("token")]
    public required string Token { get; set; }

    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>统一错误响应。</summary>
public sealed class ApiError
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}

/// <summary>REST 错误码常量。</summary>
public static class ApiErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string PairCodeInvalid = "PAIR_CODE_INVALID";
    public const string InternalError = "INTERNAL_ERROR";
}
