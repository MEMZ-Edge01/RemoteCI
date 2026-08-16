using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ScheduleSyncTaskTrackerTests
{
    [Fact]
    public async Task ActiveTask_ReturnsBusyAndCompletionReleasesWaiter()
    {
        var tracker = new ScheduleSyncTaskTracker();
        var first = tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.WebUi));
        var second = tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.Automatic));

        Assert.Equal(ScheduleSyncTaskState.Running, first.State);
        Assert.Equal(ScheduleSyncTaskState.Busy, second.State);
        Assert.Equal(first.TaskId, second.ActiveTaskId);

        var waiting = tracker.WaitForTerminalAsync(first.TaskId, TimeSpan.FromSeconds(1));
        tracker.Observe(new ScheduleSyncStatus
        {
            TaskId = first.TaskId,
            Source = first.Source,
            State = ScheduleSyncTaskState.Completed,
            Message = "完成",
            StartedAt = first.StartedAt,
            FinishedAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal(ScheduleSyncTaskState.Completed, (await waiting).State);
        Assert.Null(tracker.Current);
        Assert.Equal(ScheduleSyncTaskState.Running, tracker.TryBegin(ScheduleSyncRequest.Create(ScheduleSyncSource.Watch)).State);
    }
}