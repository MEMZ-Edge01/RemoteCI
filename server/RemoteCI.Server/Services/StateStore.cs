using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed class StateStore : IStateStore
{
    private ClassStateSnapshot? _snapshot;
    private ClassEvent? _event;
    private readonly object _lock = new();

    public void SaveSnapshot(ClassStateSnapshot snapshot) =>
        Set(ref _snapshot, snapshot);

    public ClassStateSnapshot? GetLatestSnapshot() =>
        Get(_snapshot);

    public void SaveEvent(ClassEvent @event) =>
        Set(ref _event, @event);

    public ClassEvent? GetLatestEvent() =>
        Get(_event);

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
