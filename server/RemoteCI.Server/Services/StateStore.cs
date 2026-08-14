using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed class StateStore : IStateStore
{
    private ClassStateSnapshot? _snapshot;
    private ScheduleBundle? _schedule;
    private ClassEvent? _event;
    private IReadOnlyList<ExtensionDefinition>? _extensions;
    private readonly object _lock = new();

    public void SaveSnapshot(ClassStateSnapshot snapshot) =>
        Set(ref _snapshot, snapshot);

    public ClassStateSnapshot? GetLatestSnapshot() =>
        Get(_snapshot);

    public void SaveSchedule(ScheduleBundle schedule) =>
        Set(ref _schedule, schedule);

    public ScheduleBundle? GetLatestSchedule() =>
        Get(_schedule);

    public void SaveEvent(ClassEvent @event) =>
        Set(ref _event, @event);

    public void SaveExtensions(IReadOnlyList<ExtensionDefinition> extensions) =>
        Set(ref _extensions, extensions);

    public IReadOnlyList<ExtensionDefinition>? GetLatestExtensions() =>
        Get(_extensions);

    private void Set<T>(ref T? field, T value) where T : class
    {
        lock (_lock)
        {
            field = value;
        }
    }

    private T? Get<T>(T? field) where T : class
    {
        lock (_lock)
        {
            return field;
        }
    }
}
