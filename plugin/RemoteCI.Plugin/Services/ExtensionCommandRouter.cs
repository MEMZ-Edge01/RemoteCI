using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// RunExtension 命令的执行路由：按扩展声明的最小权限动态校验，
/// 再调用注册方回调，并把异常统一转换为 CommandResult。
/// </summary>
internal sealed class ExtensionCommandRouter
{
    private readonly IRemoteCiExtensionRegistry _registry;
    private readonly ILogger<ExtensionCommandRouter> _logger;

    public ExtensionCommandRouter(
        IRemoteCiExtensionRegistry registry,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _logger = loggerFactory.CreateLogger<ExtensionCommandRouter>();
    }

    public async Task<CommandResult> RunAsync(CommandMessage command)
    {
        if (command.RequestedBy is null)
            return Failure(CommandResultCodes.Forbidden, "权限不足");

        var id = command.ExtensionId;
        if (string.IsNullOrWhiteSpace(id))
            return Failure(CommandResultCodes.InvalidRequest, "缺少扩展 Id");

        var extension = _registry.GetExtensions()
            .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (extension is null)
            return Failure(CommandResultCodes.InvalidRequest, $"扩展功能不存在：{id}");
        if (!command.RequestedBy.Permissions.HasFlag(extension.RequiredPermission))
            return Failure(CommandResultCodes.Forbidden, "权限不足");

        var args = command.ExtensionArgs ?? new Dictionary<string, string?>();
        var missing = extension.Parameters
            .Where(p => p.Required && string.IsNullOrWhiteSpace(args.GetValueOrDefault(p.Key)))
            .Select(p => p.Label)
            .ToList();
        if (missing.Count > 0)
            return Failure(CommandResultCodes.InvalidRequest, $"缺少参数：{string.Join("、", missing)}");

        try
        {
            var result = await extension.ExecuteAsync(
                new ExtensionExecutionContext { RequestedBy = command.RequestedBy },
                args,
                CancellationToken.None);
            return result ?? Failure(CommandResultCodes.InternalError, "扩展未返回执行结果");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemoteCI 扩展执行失败：{ExtensionId}", id);
            return Failure(CommandResultCodes.InternalError, "扩展执行异常，请查看 ClassIsland 日志");
        }
    }

    private static CommandResult Failure(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };
}
