using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>识别无参数的课表拉取消息，并把实际收集调度留给插件编排器。</summary>
internal sealed class SchedulePullRequestHandler(Action requestFreshSchedule)
{
    public bool TryHandle(Envelope envelope)
    {
        if (envelope.Type != Protocol.MessageTypeSchedulePull) return false;
        requestFreshSchedule();
        return true;
    }
}
