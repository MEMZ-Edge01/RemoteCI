# RemoteCI

RemoteCI 是 ClassIsland 2.x、ASP.NET Core 服务端和 Wear OS 手表组成的课表联动系统，统一使用账号、设备会话和细粒度权限控制公网与局域网操作。

## 已实现

- 服务端：ASP.NET Core Identity、SQLite 迁移、PBKDF2 密码哈希、1 小时访问令牌、30 天可撤销设备会话和完整 Razor WebUI。
- 权限：管理员固定拥有全部权限；普通用户默认只查看当前课程，可附加 `AccessWebUi`、`ManageUsers`、`SendNotifications`、`ManageSchedule`。
- 同步：服务端是账号与权限唯一真源，通过插件 WebSocket 自动同步不含密码的授权镜像；镜像离线超过 24 小时后只允许课程展示。
- 局域网：手表不发送密码或云端访问令牌，使用设备会话派生验证器完成一次性 HMAC 挑战认证。
- 课表：状态按秒推送，七日课表单独低频同步；支持指定日期交换两节课或替换科目，并用修订号防止并发覆盖。
- 通知：WebUI 或手表发送的消息先由 ClassIsland 正式通知提供方显示，成功后再广播给手表。
- 手表：设备密钥由 Android Keystore AES-GCM 保护；普通用户界面只显示当前课程，五类消息可按设备单独开关。
- 更新：WebUI 个人账号与手表设置中可从 GitHub 最新 release 一键检查并更新；插件升级由 ClassIsland 插件市场统一管理。

不存在真实 ClassIsland 写入能力的“切换单双周”功能已经移除。

## 目录

| 目录 | 内容 |
| --- | --- |
| `shared/` | C# 协议 v2 与共享 DTO |
| `server/` | ASP.NET Core 服务端、Razor WebUI、Identity/SQLite |
| `plugin/` | ClassIsland 2.x 插件与 CIPX 构建 |
| `wearos/` | Kotlin/Compose for Wear OS 应用 |
| `docs/` | 协议、部署和平台说明 |

## 构建与测试

```powershell
dotnet restore RemoteCI.slnx
dotnet test RemoteCI.slnx -c Release --no-restore

dotnet build plugin/RemoteCI.Plugin/RemoteCI.Plugin.csproj -c Release
dotnet build plugin/RemoteCI.Plugin/RemoteCI.Plugin.csproj -c Release -p:CreateCipx=true

cd wearos
$env:JAVA_HOME="C:\path\to\jdk-17"
.\gradlew.bat testDebugUnitTest assembleDebug
```

手表构建需要 JDK 17 和 Android SDK；本机开发脚本见 `wearos/dev.ps1`。

部署和首次配对见 [部署文档](docs/deployment.md)，消息格式见 [协议 v2](docs/protocol.md)。

## 许可

项目使用 GPLv3，第三方依赖见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
