using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>
/// 插件注册的自定义远程功能元数据，经 extensions_sync 同步给服务端与手表。
/// 手表端据此在控制菜单底部渲染入口和参数表单。
/// </summary>
public sealed class ExtensionDefinition
{
    /// <summary>全局唯一扩展 Id，命令路由与去重都使用它。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>手表菜单显示的文案。</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>可选 Material 图标名；手表端命中白名单时显示图标，否则纯文字。</summary>
    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; set; }

    /// <summary>执行所需的最小权限；手表显示与插件执行端都会校验。</summary>
    [JsonPropertyName("requiredPermission")]
    public UserPermissions RequiredPermission { get; set; }

    /// <summary>可选参数表单描述；为空时手表点击后直接执行。</summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtensionParameter>? Parameters { get; set; }
}

/// <summary>扩展参数表单的单个字段描述（schema 驱动，手表通用渲染）。</summary>
public sealed class ExtensionParameter
{
    /// <summary>参数键，命令中 extensionArgs 的 key。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>手表表单上展示的字段名。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public ExtensionParameterType Type { get; set; } = ExtensionParameterType.Text;

    /// <summary>默认值；switch 使用 "true"/"false"，其余使用字符串。</summary>
    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>select 类型的候选项。</summary>
    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Options { get; set; }
}

/// <summary>扩展参数类型。</summary>
public enum ExtensionParameterType
{
    Text = 1,
    Number = 2,
    Switch = 3,
    Select = 4,
}
