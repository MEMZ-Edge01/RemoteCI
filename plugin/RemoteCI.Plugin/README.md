# RemoteCI ClassIsland 插件

插件从 ClassIsland 2.x 公共服务读取当前状态和未来七日课表，并负责执行有权限的远程换课与通知命令。

## 安全边界

- 插件从不接收或保存学生密码及密码哈希。
- 云端使用一次性插件配对码换取长期插件凭据。
- 局域网使用服务端同步的设备会话验证器进行一次性 HMAC 挑战认证。
- 授权镜像超过 24 小时未更新时，所有管理命令都会被拒绝。
- 所有命令在插件执行端再次鉴权，客户端隐藏按钮不构成安全控制。

## 功能

- 每秒推送当前课程状态，独立同步未来七日课表。
- 为指定日期创建 ClassIsland 临时课表层，支持交换和替换，并保存 Profile。
- 比较 `expectedRevision`，拒绝过期换课请求。
- 通过 `NotificationProviderBase` 显示自定义通知，成功后广播事件。
- 旁路观察 ClassIsland 的统一通知入口，把自动化“显示提醒”和第三方插件通知广播到手表，不接管或延迟桌面端显示。

## 配置

在服务端 WebUI 的“概览”页面生成一次性插件配对码，再在 ClassIsland 的 RemoteCI 设置中填写云端地址和配对码并重启 ClassIsland。设置页会实时显示云端服务器正在连接、已连接、等待配对或等待重试，并显示脱敏后的最近一次连接错误；完整异常仍保留在 ClassIsland 日志中。点击“测试服务器连接”会验证当前真实 WebSocket 通道；断线时会跳过自动重连退避并立即尝试配对、鉴权和初始化，而不是只探测 Web 页面是否可访问。

云端 WebSocket 握手明确返回 `401` 或 `403` 时，插件会删除被拒绝的长期凭据；若设置中已有新配对码，下一次重试会立即用它重新配对。其他非 WebSocket 响应不会误删凭据。

服务启动后，可在同一设置页点击“立即推送当前课表”，强制重新生成七日课表并发送到已连接的服务端和手表。插件设置页会显示任务成功、失败或超时，不再永久停留在“正在推送”；如果 WebUI、手表、自动拉取或连接初始化已有任务运行，按钮会显示占用来源并拒绝重复推送。

## 为其他插件提供扩展

RemoteCI 插件公开了一组扩展接口，其他 ClassIsland 插件可以把自定义远程功能注册进来，功能会自动出现在 WearOS 控制子菜单底部，点击后由注册方回调执行。

### 扩展接口

- `IRemoteCiExtension`：功能定义（`Id`、`DisplayName`、兼容字段 `RequiredPermission`，可选 `Icon` 与 `Parameters`，以及 `ExecuteAsync` 执行回调）。`Id` 必须非空、无首尾空白且不超过 200 个字符。
- `IRemoteCiExtensionRegistry`：注册 / 注销 / 查询与变更事件，RemoteCI 已注册为单例服务。
- `RemoteCiExtensionBase`：推荐基类，只需实现核心成员，其余按无图标、无参数处理。

其他插件项目需在编译期引用 `RemoteCI.Plugin.dll`，并在 ClassIsland `AppStarted` 之后获取注册表（此时主机容器已构建完成）：

```csharp
using ClassIsland.Shared;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

public sealed class LockScreenExtension : RemoteCiExtensionBase
{
    public override string Id => "myplugin.lock";
    public override string DisplayName => "锁屏";
    public override UserPermissions RequiredPermission => UserPermissions.RunExtensions;
    public override string? Icon => "power";

    public override Task<CommandResult> ExecuteAsync(
        ExtensionExecutionContext context,
        IReadOnlyDictionary<string, string?> args,
        CancellationToken cancellationToken)
    {
        // context.RequestedBy 是已认证的发起用户，可在这里执行远程功能。
        return Task.FromResult(new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = "已锁屏",
        });
    }
}

// 插件入口的 AppStarted 事件中注册：
var registry = IAppHost.GetService<IRemoteCiExtensionRegistry>();
registry?.Register(new LockScreenExtension());
```

### 参数表单（可选）

扩展可声明 `Parameters` 列表，手表会按 schema 渲染参数输入页，用户填写后以 `extensionArgs` 字典传入 `ExecuteAsync`（键为参数 `Key`，值统一为字符串）：

| 类型 | 手表呈现 |
| --- | --- |
| `Text` | 单行文本输入 |
| `Number` | 数字输入 |
| `Switch` | 开关（值为 "true"/"false"） |
| `Select` | 候选项循环切换（需提供 `Options`） |

```csharp
public override IReadOnlyList<ExtensionParameter> Parameters => new[]
{
    new ExtensionParameter
    {
        Key = "message",
        Label = "通知内容",
        Type = ExtensionParameterType.Text,
        Required = true,
        DefaultValue = "下课了",
    },
    new ExtensionParameter
    {
        Key = "urgent",
        Label = "紧急",
        Type = ExtensionParameterType.Switch,
        DefaultValue = "false",
    },
    new ExtensionParameter
    {
        Key = "voice",
        Label = "播报音色",
        Type = ExtensionParameterType.Select,
        Options = ["标准", "柔和"],
    },
};
```

### 安全边界

- 扩展调用必须具备独立的 `RunExtensions` 权限并通过管理员为该扩展设置的开放策略；`RequiredPermission` 仅为旧扩展兼容字段，不再额外关联通知、电源等权限。账号还可在 WebUI 决定是否显示在自己的手表上。
- 手表端只负责隐藏入口，服务端与插件执行端都会再次校验，隐藏按钮不构成安全控制。
- 授权镜像超过 24 小时未更新时，局域网直连会拒绝执行任何扩展命令。
- `ExecuteAsync` 抛出的异常统一转换为 `INTERNAL_ERROR` 回执，不会中断 RemoteCI 插件。

构建：

```powershell
dotnet build plugin/RemoteCI.Plugin/RemoteCI.Plugin.csproj -c Release
dotnet build plugin/RemoteCI.Plugin/RemoteCI.Plugin.csproj -c Release -p:CreateCipx=true
```
