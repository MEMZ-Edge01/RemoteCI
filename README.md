# RemoteCI

![CI](https://github.com/MEMZ-Edge01/RemoteCI/actions/workflows/ci.yml/badge.svg)

RemoteCI 是 ClassIsland 2.x、ASP.NET Core 服务端和 Wear OS 手表组成的课表联动系统，统一使用账号、设备会话和细粒度权限控制公网与局域网操作。

## 已实现

- 服务端：ASP.NET Core Identity、SQLite 迁移、PBKDF2 密码哈希、1 小时访问令牌、30 天可撤销设备会话和完整 Razor WebUI。
- 权限：管理员固定拥有全部权限；普通用户默认可查看当前课程和 WebUI 七日课表，可分别附加概览、人员管理、发送与清除通知、老师来了、换课、扩展功能、主界面和电源控制权限。
- 同步：服务端是账号与权限唯一真源，通过插件 WebSocket 自动同步不含密码的授权镜像；镜像离线超过 24 小时后只允许课程展示。
- 局域网：手表不发送密码或云端访问令牌，使用设备会话派生验证器完成一次性 HMAC 挑战认证；登录页可扫描同一局域网的插件（UDP 发现 + 引导）自动填写云端地址与电脑 IP。
- 课表：状态按秒推送，七日课表单独低频同步；所有已登录账号都能在 WebUI 查看并手动拉取七日课表，拥有换课权限的账号还可配置自动拉取或修改课表；插件推送、WebUI 拉取、手表拉取、自动拉取和连接初始化共享同一个任务锁，运行中会在各端显示来源并拒绝重复任务；换课用修订号防止并发覆盖。
- 通知：WebUI 或手表发送的消息先由 ClassIsland 正式通知提供方显示，成功后再广播给手表。
- 手表：设备密钥由 Android Keystore AES-GCM 保护；普通用户界面只显示当前课程，五类消息可按设备单独开关；可在“设置 → 外观”切换 Material Design 3 配色方案，并自动适配圆形与矩形屏幕；云端中转默认保持开启，关闭入口仅位于开发者设置，且密码登录始终可临时使用云端完成认证。
- 更新：Windows、Linux 与 Docker WebUI 可安全更新，WebUI 与手表均可选择正式版/Beta 渠道并对同版本强制覆盖；服务端只替换实际程序集目录，Development 环境禁用在线覆盖；fnOS 由应用商店管理；手表仍只会安装不高于所连接 WebUI 的版本；插件由 ClassIsland 插件市场管理。

不存在真实 ClassIsland 写入能力的“切换单双周”功能已经移除。

## 目录

| 目录 | 内容 |
| --- | --- |
| `shared/` | C# V3.1 协议与共享 DTO |
| `server/` | ASP.NET Core 服务端、Razor WebUI、Identity/SQLite |
| `plugin/` | ClassIsland 2.x 插件与 CIPX 构建 |
| `wearos/` | Kotlin/Compose for Wear OS 应用 |
| `fnos/` | 飞牛 fnOS 应用（fpk）工程与打包脚本 |
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

部署和首次配对见 [部署文档](docs/deployment.md)，消息格式见 [协议 V3.1](docs/protocol.md)。

飞牛 fnOS 用户可以从 GitHub Releases 下载 `RemoteCI-<版本>.fpk` 直接在应用中心安装，
安装、更新与开发说明见 [fnos/README.md](fnos/README.md)。

## 许可

项目使用 GPLv3，第三方依赖见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。


## 一键构建 CIPX

Windows 下双击仓库根目录的 `Build-Latest-Cipx.cmd`，脚本会先弹出“另存为”对话框。选择保存位置后，脚本会读取 `plugin/RemoteCI.Plugin/manifest.yml` 中的当前版本，自动执行 Release 构建并将最新 `.cipx` 复制到选定位置。

构建成功后会显示实际保存路径、插件版本和 SHA-256 校验值。构建失败时会弹出错误提示，并保留命令窗口供查看详细输出。

脚本也支持命令行调用，便于自动化验证：

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\Build-Latest-Cipx.ps1 -OutputPath C:\Temp\RemoteCI.Plugin.cipx -NoPrompt
```
