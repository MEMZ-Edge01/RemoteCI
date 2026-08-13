using System.Diagnostics;
using System.Text.Json;

namespace RemoteCI.Server.Services;

/// <summary>由独立进程执行的更新计划；服务端退出后再替换 Windows 锁定文件。</summary>
public sealed class UpdateInstallPlan
{
    public required string SourceDirectory { get; init; }
    public required string DestinationDirectory { get; init; }
    public required string DotnetHostPath { get; init; }
    public required string ServerAssemblyPath { get; init; }
    public required string LogPath { get; init; }
    public int ServerProcessId { get; init; }
    public string[] ServerArguments { get; init; } = [];
}

/// <summary>
/// RemoteCI.Server.dll 的轻量更新入口。通过 <c>--apply-update</c> 启动时不会创建 Web 主机，
/// 只等待旧服务端退出、覆盖文件并重新拉起服务端。
/// </summary>
public static class UpdateInstaller
{
    public const string Command = "--apply-update";
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromMinutes(2);
    private const int CopyRetryCount = 120;

    public static async Task<bool> TryRunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length != 2 || !string.Equals(args[0], Command, StringComparison.Ordinal)) return false;

        UpdateInstallPlan? plan = null;
        try
        {
            await using var stream = File.OpenRead(args[1]);
            plan = await JsonSerializer.DeserializeAsync<UpdateInstallPlan>(stream, cancellationToken: ct)
                ?? throw new InvalidDataException("更新计划为空。");
            await ExecuteAsync(plan, ct);
        }
        catch (Exception ex)
        {
            if (plan is not null) AppendLog(plan.LogPath, $"更新失败：{ex}");
            else Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
        return true;
    }

    public static async Task ExecuteAsync(UpdateInstallPlan plan, CancellationToken ct = default)
    {
        Validate(plan);
        AppendLog(plan.LogPath, $"等待服务端进程 {plan.ServerProcessId} 退出。");
        await WaitForExitAsync(plan.ServerProcessId, ct);
        await ApplyFilesAsync(plan.SourceDirectory, plan.DestinationDirectory, ct);
        AppendLog(plan.LogPath, "更新文件替换完成。");

        var startInfo = new ProcessStartInfo(plan.DotnetHostPath)
        {
            WorkingDirectory = plan.DestinationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(plan.ServerAssemblyPath);
        foreach (var argument in plan.ServerArguments) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("更新完成，但无法重新启动 RemoteCI 服务端。");
        AppendLog(plan.LogPath, $"服务端已重新启动，进程 {process.Id}。");
    }

    /// <summary>复制发布目录；短暂文件锁会持续重试，确保旧进程完全释放原生 DLL。</summary>
    public static async Task ApplyFilesAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < CopyRetryCount)
                {
                    await Task.Delay(250, ct);
                }
            }
        }
    }

    private static async Task WaitForExitAsync(int processId, CancellationToken ct)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(ct).WaitAsync(ProcessExitTimeout, ct);
        }
        catch (ArgumentException)
        {
            // 旧进程已经退出，直接安装。
        }
    }

    private static void Validate(UpdateInstallPlan plan)
    {
        var source = Path.GetFullPath(plan.SourceDirectory);
        var destination = Path.GetFullPath(plan.DestinationDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"更新源目录不存在：{source}");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新源目录不能与运行目录相同。");
        if (Path.GetPathRoot(destination) == destination)
            throw new InvalidDataException("拒绝把磁盘根目录作为更新目标。");
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能阻止已验证的更新流程。
        }
    }
}
