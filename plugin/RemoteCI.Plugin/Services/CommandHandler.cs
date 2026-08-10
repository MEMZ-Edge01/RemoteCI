using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using RemoteCI.Plugin.Settings;
using System.Text.Json;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 指令处理器：执行手表发来的控制指令。
/// v0.1：切换周次通过本地覆盖生效；临时换课先记录回执，
/// v0.2 将接入 ClassIsland ProfileService 实现真实换课。
/// </summary>
public sealed class CommandHandler
{
    private readonly PluginSettings _settings;
    private readonly ILogger<CommandHandler> _logger;
    private readonly object _lock = new();
    private int? _weekOverride;

    public CommandHandler(PluginSettings settings, ILogger<CommandHandler> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>当前周次覆盖值；null 表示使用 ClassIsland 自动计算的周次。</summary>
    public int? WeekOverride
    {
        get
        {
            lock (_lock)
            {
                return _weekOverride;
            }
        }
    }

    /// <summary>执行指令并返回回执。</summary>
    public CommandResult Handle(CommandMessage command)
    {
        try
        {
            return command.Command switch
            {
                CommandKind.SwitchWeek => HandleSwitchWeek(command),
                CommandKind.TempSwapClass => HandleTempSwap(command),
                _ => new CommandResult { Success = false, Message = $"未知指令：{command.Command}" },
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "指令执行失败：{Command}", command.Command);
            return new CommandResult { Success = false, Message = "指令执行异常" };
        }
    }

    private CommandResult HandleSwitchWeek(CommandMessage command)
    {
        // 参数 targetWeek 可选：缺省时在 1/2 之间自动切换。
        var targetWeek = TryGetInt(command.Parameters, "targetWeek");
        lock (_lock)
        {
            var next = targetWeek ?? (_weekOverride is 1 ? 2 : 1);
            _weekOverride = next;
            _logger.LogInformation("周次已切换为 {Week}（配对码 {PairCode}）", next, _settings.PairCode);
            return new CommandResult { Success = true, Message = $"已切换到第 {next} 周" };
        }
    }

    private CommandResult HandleTempSwap(CommandMessage command)
    {
        var from = TryGetString(command.Parameters, "from") ?? "?";
        var to = TryGetString(command.Parameters, "to") ?? "?";
        _logger.LogInformation("收到换课请求：{From} → {To}（v0.2 将接入 ProfileService 真实换课）", from, to);
        return new CommandResult
        {
            Success = true,
            Message = $"已记录换课请求（{from} → {to}），真实换课将在 v0.2 支持",
        };
    }

    private static int? TryGetInt(Dictionary<string, object> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int n => n,
            string s => int.TryParse(s, out var n) ? n : null,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var n) => n,
            JsonElement { ValueKind: JsonValueKind.String } je
                => int.TryParse(je.GetString(), out var n) ? n : null,
            _ => null,
        };
    }

    private static string? TryGetString(Dictionary<string, object> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString(),
            _ => value.ToString(),
        };
    }
}
