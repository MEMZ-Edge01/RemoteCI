using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed class StateStore : IStateStore
{
    private ClassStateSnapshot? _snapshot;
    private ScheduleBundle? _schedule;
    private ClassEvent? _event;
    private IReadOnlyList<ExtensionDefinition>? _extensions;

    // 单引用原子发布：读写都是整体替换不可变快照，Volatile 保证跨线程可见性，无需锁。
    public void SaveSnapshot(ClassStateSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

    public ClassStateSnapshot? GetLatestSnapshot() => Volatile.Read(ref _snapshot);

    public void SaveSchedule(ScheduleBundle schedule) => Volatile.Write(ref _schedule, schedule);

    public ScheduleBundle? GetLatestSchedule() => Volatile.Read(ref _schedule);

    public void SaveEvent(ClassEvent @event) => Volatile.Write(ref _event, @event);

    public void SaveExtensions(IReadOnlyList<ExtensionDefinition> extensions) => Volatile.Write(ref _extensions, extensions);

    public IReadOnlyList<ExtensionDefinition>? GetLatestExtensions() => Volatile.Read(ref _extensions);
}
