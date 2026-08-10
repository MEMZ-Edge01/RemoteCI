using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 控制指令（command 载荷）：手表发起，服务端转发，插件执行。
/// </summary>
public sealed class CommandMessage
{
    [JsonPropertyName("command")]
    public CommandKind Command { get; set; }

    /// <summary>指令参数，结构随 command 类型变化（见 CommandKind 注释）。</summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>执行结果（插件回执，经服务端转发回发起端）。</summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CommandResult? Result { get; set; }
}

/// <summary>
/// 指令执行结果。
/// </summary>
public sealed class CommandResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
