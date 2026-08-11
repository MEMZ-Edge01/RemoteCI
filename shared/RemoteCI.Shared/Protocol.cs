namespace RemoteCI.Shared;

/// <summary>三端共同使用的 v2 协议常量。</summary>
public static class Protocol
{
    public const int Version = 2;

    public const string MessageTypeStatePush = "state_push";
    public const string MessageTypeScheduleSync = "schedule_sync";
    public const string MessageTypeEventNotify = "event_notify";
    public const string MessageTypeCommand = "command";
    public const string MessageTypeCommandResult = "command_result";
    public const string MessageTypeAuthChallenge = "auth_challenge";
    public const string MessageTypeAuthProof = "auth_proof";
    public const string MessageTypeAuthState = "auth_state";
    public const string MessageTypeAccountSync = "account_sync";

    public const string QueryToken = "token";
    public const string HeaderAuthorization = "Authorization";
    public const string BearerScheme = "Bearer";
}

public enum PeerRole
{
    Plugin = 1,
    Watch = 2,
}

public enum UserRole
{
    User = 1,
    Admin = 2,
}

/// <summary>
/// 有效权限位。管理员的有效权限固定为 All；普通用户至少拥有 ViewCurrentCourse。
/// </summary>
[Flags]
public enum UserPermissions
{
    None = 0,
    ViewCurrentCourse = 1 << 0,
    AccessWebUi = 1 << 1,
    ManageUsers = 1 << 2,
    SendNotifications = 1 << 3,
    ManageSchedule = 1 << 4,
    SystemControl = 1 << 5,
    All = ViewCurrentCourse | AccessWebUi | ManageUsers | SendNotifications | ManageSchedule | SystemControl,
}

public static class RolePermissions
{
    public static UserPermissions Effective(UserRole role, UserPermissions granted) => role == UserRole.Admin
        ? UserPermissions.All
        : UserPermissions.ViewCurrentCourse | (granted & ~UserPermissions.ViewCurrentCourse);

    public static bool Has(UserProfileLike user, UserPermissions permission) =>
        (Effective(user.Role, user.GrantedPermissions) & permission) == permission;
}

/// <summary>供共享权限计算使用的小接口，避免服务端实体依赖传输模型。</summary>
public interface UserProfileLike
{
    UserRole Role { get; }
    UserPermissions GrantedPermissions { get; }
}

public enum ClassStateKind
{
    None = 0,
    Class = 1,
    Breaking = 2,
    AfterSchool = 3,
    PrepareClass = 4,
}

public enum ClassEventKind
{
    OnClass = 1,
    OnBreaking = 2,
    OnAfterSchool = 3,
    ScheduleChanged = 4,
    Custom = 5,
    AutomationNotification = 6,
    PluginNotification = 7,
}

public enum CommandKind
{
    ChangeSchedule = 1,
    SendNotification = 2,
    ClearNotifications = 3,
    SetMainMenuVisibility = 4,
    Power = 5,
    Volume = 6,
}

public enum PowerActionKind
{
    Shutdown = 1,
    Restart = 2,
    Sleep = 3,
    Hibernate = 4,
}

public static class CommandPermissions
{
    public static UserPermissions Required(CommandKind command) => command switch
    {
        CommandKind.ChangeSchedule => UserPermissions.ManageSchedule,
        CommandKind.SendNotification or CommandKind.ClearNotifications => UserPermissions.SendNotifications,
        CommandKind.SetMainMenuVisibility or CommandKind.Power or CommandKind.Volume => UserPermissions.SystemControl,
        _ => UserPermissions.None,
    };
}

public enum ScheduleChangeMode
{
    Exchange = 1,
    Replace = 2,
}
