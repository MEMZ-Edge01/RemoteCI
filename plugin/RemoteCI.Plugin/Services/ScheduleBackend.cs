using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 课表读取的最小防腐层：隔离 ClassIsland 服务接口（其含 internal 成员，外部程序集无法实现假对象），
/// 让课表构建与修订号逻辑可单元测试。生产由 ScheduleBackendAdapter 适配真实服务。
/// </summary>
public interface IScheduleBackend
{
    IReadOnlyDictionary<Guid, Subject> Subjects { get; }
    ClassPlan? GetClassPlan(DateTime date, out Guid? planId);
}

public sealed class ScheduleBackendAdapter(ILessonsService lessons, IProfileService profiles) : IScheduleBackend
{
    public IReadOnlyDictionary<Guid, Subject> Subjects => profiles.Profile.Subjects;
    public ClassPlan? GetClassPlan(DateTime date, out Guid? planId) => lessons.GetClassPlanByDate(date, out planId);
}
