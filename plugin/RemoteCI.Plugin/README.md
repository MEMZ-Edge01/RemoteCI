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

在服务端 WebUI 的“人员权限”页面生成一次性插件配对码，再在 ClassIsland 的 RemoteCI 设置中填写云端地址和配对码并重启 ClassIsland。

构建：

```powershell
dotnet build plugin/RemoteCI.Plugin/RemoteCI.Plugin.csproj -c Release
dotnet build plugin/RemoteCI.Plugin/RemoteCI.Plugin.csproj -c Release -p:CreateCipx=true
```
