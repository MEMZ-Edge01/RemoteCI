using System.Collections.Concurrent;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>服务端保存当前课表任务并为 WebUI 提供可等待的终态。</summary>
public sealed class ScheduleSyncTaskTracker
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ScheduleSyncStatus>> _waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScheduleSyncStatus> _terminal = new(StringComparer.Ordinal);
    private ScheduleSyncStatus? _current;

    public ScheduleSyncStatus? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public ScheduleSyncStatus TryBegin(ScheduleSyncRequest request)
    {
        lock (_gate)
        {
            if (_current is { State: ScheduleSyncTaskState.Running } active)
            {
                return new ScheduleSyncStatus
                {
                    TaskId = request.TaskId,
                    Source = request.Source,
                    State = ScheduleSyncTaskState.Busy,
                    Message = $"已有{SourceName(active.Source)}课表任务正在执行，请稍候",
                    StartedAt = active.StartedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    ActiveTaskId = active.TaskId,
                };
            }

            _current = new ScheduleSyncStatus
            {
                TaskId = request.TaskId,
                Source = request.Source,
                State = ScheduleSyncTaskState.Running,
                Message = $"正在连接插件执行{SourceName(request.Source)}任务",
                StartedAt = DateTimeOffset.UtcNow,
            };
            return _current;
        }
    }

    public void Observe(ScheduleSyncStatus status)
    {
        lock (_gate)
        {
            if (status.State == ScheduleSyncTaskState.Running)
                _current = status;
            else if (_current?.TaskId == status.TaskId)
            {
                _current = status.State == ScheduleSyncTaskState.Busy && !string.IsNullOrWhiteSpace(status.ActiveTaskId)
                    ? new ScheduleSyncStatus
                    {
                        TaskId = status.ActiveTaskId,
                        Source = ScheduleSyncSource.Unknown,
                        State = ScheduleSyncTaskState.Running,
                        Message = status.Message,
                        StartedAt = status.StartedAt,
                    }
                    : null;
            }
        }

        if (status.State is ScheduleSyncTaskState.Completed or ScheduleSyncTaskState.Failed or ScheduleSyncTaskState.Busy)
        {
            RememberTerminal(status);
            if (_waiters.TryRemove(status.TaskId, out var waiter)) waiter.TrySetResult(status);
        }
    }

    private void RememberTerminal(ScheduleSyncStatus status)
    {
        _terminal[status.TaskId] = status;
        if (_terminal.Count <= 64) return;
        foreach (var stale in _terminal.Values
                     .OrderBy(x => x.FinishedAt ?? DateTimeOffset.MaxValue)
                     .Take(_terminal.Count - 64))
            _terminal.TryRemove(stale.TaskId, out _);
    }

    public async Task<ScheduleSyncStatus> WaitForTerminalAsync(
        string taskId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_terminal.TryRemove(taskId, out var completed)) return completed;
        var waiter = _waiters.GetOrAdd(taskId, _ =>
            new TaskCompletionSource<ScheduleSyncStatus>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_terminal.TryRemove(taskId, out completed)) waiter.TrySetResult(completed);
        try
        {
            return await waiter.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _waiters.TryRemove(taskId, out _);
            _terminal.TryRemove(taskId, out _);
        }
    }

    internal static string SourceName(ScheduleSyncSource source) => source switch
    {
        ScheduleSyncSource.Plugin => "插件端推送",
        ScheduleSyncSource.WebUi => "WebUI 拉取",
        ScheduleSyncSource.Watch => "手表端拉取",
        ScheduleSyncSource.Automatic => "自动拉取",
        ScheduleSyncSource.Connection => "连接初始化拉取",
        _ => "课表同步",
    };
}
