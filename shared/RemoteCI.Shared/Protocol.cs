namespace RemoteCI.Shared;

/// <summary>
/// 协议级常量。协议 v1 冻结后，任何新增字段必须保持可选，不得破坏老客户端解析。
/// </summary>
public static class Protocol
{
    /// <summary>当前协议版本号。</summary>
    public const int Version = 1;

    /// <summary>WebSocket 消息类型：状态推送。</summary>
    public const string MessageTypeStatePush = "state_push";

    /// <summary>WebSocket 消息类型：课程事件通知。</summary>
    public const string MessageTypeEventNotify = "event_notify";

    /// <summary>WebSocket 消息类型：控制指令。</summary>
    public const string MessageTypeCommand = "command";

    /// <summary>WebSocket 查询参数中携带认证 token 的参数名。</summary>
    public const string QueryToken = "token";

    /// <summary>HTTP 请求头中携带认证 token 的请求头名。</summary>
    public const string HeaderAuthorization = "Authorization";

    /// <summary>认证 scheme 前缀（Bearer）。</summary>
    public const string BearerScheme = "Bearer";
}

/// <summary>
/// 连接角色：插件（数据提供方）或手表（数据消费方）。
/// </summary>
public enum PeerRole
{
    Plugin = 1,
    Watch = 2,
}

/// <summary>
/// 课程时间状态，与 ClassIsland 的 TimeState 语义对齐。
/// </summary>
public enum ClassStateKind
{
    /// <summary>未加载课表 / 未知状态。</summary>
    None = 0,

    /// <summary>上课中。</summary>
    Class = 1,

    /// <summary>课间休息。</summary>
    Breaking = 2,

    /// <summary>已放学（超出今日时间表）。</summary>
    AfterSchool = 3,
}

/// <summary>
/// 课程事件类型，对应 ClassIsland LessonsService 事件。
/// </summary>
public enum ClassEventKind
{
    /// <summary>进入上课时间点。</summary>
    OnClass = 1,

    /// <summary>进入课间休息时间点。</summary>
    OnBreaking = 2,

    /// <summary>放学。</summary>
    OnAfterSchool = 3,

    /// <summary>当前时间状态改变。</summary>
    StateChanged = 4,
}

/// <summary>
/// 手表可发送的控制指令类型。
/// </summary>
public enum CommandKind
{
    /// <summary>切换周次（单双周/多周轮换）。参数：targetWeek（int，可空，缺省为自动切换）。</summary>
    SwitchWeek = 1,

    /// <summary>临时换课。参数：from（string，源节次标识）、to（string，目标节次标识）。</summary>
    TempSwapClass = 2,
}
