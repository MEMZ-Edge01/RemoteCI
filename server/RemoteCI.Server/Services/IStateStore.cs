using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 内存状态缓存：保存插件推送的最新快照与最近事件，供新连接/HTTP 查询获取。
/// 单班级场景，后续多班级扩展为按 room/class 维度分桶。
/// </summary>
public interface IStateStore
{
    void SaveSnapshot(ClassStateSnapshot snapshot);
    ClassStateSnapshot? GetLatestSnapshot();
    void SaveSchedule(ScheduleBundle schedule);
    ScheduleBundle? GetLatestSchedule();
    void SaveEvent(ClassEvent @event);
    ClassEvent? GetLatestEvent();
    void SaveExtensions(IReadOnlyList<ExtensionDefinition> extensions);
    IReadOnlyList<ExtensionDefinition>? GetLatestExtensions();
}
