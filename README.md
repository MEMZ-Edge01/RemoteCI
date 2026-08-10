# RemoteCI

为 [ClassIsland 2.x](https://classisland.tech/) 开发的课表手表联动系统：电脑上的 ClassIsland 将课表状态实时推送至手表（Wear OS），手表可查看当前课/下一节课/倒计时、接收上课/下课通知（通知+振动），并可反向切换单双周、临时换课。

> ⚠️ 本项目当前为 v0.1 demo：完整混合链路（局域网直连 + 云端中转）已打通，Tiles 小组件、小米 HyperOS 适配、watchOS 端等为后续迭代。

## 仓库结构（monorepo）

| 目录 | 说明 |
| --- | --- |
| `plugin/` | ClassIsland 2.x 插件（.NET 10，cipx 包） |
| `server/` | ASP.NET Core 中转服务端（WebSocket + REST + token） |
| `wearos/` | Wear OS 手表端（Kotlin + Compose for Wear OS） |
| `shared/` | 插件与服务端共享的 C# 协议库 |
| `docs/` | 架构、协议、部署、平台适配笔记 |

## 架构概览

```text
┌─────────────┐   WebSocket(局域网)   ┌──────────────┐
│ ClassIsland │◄────────────────────►│  Wear OS 手表 │
│  插件(电脑)  │                      └──────┬───────┘
└──────┬──────┘                             │
       │ WebSocket(公网)                    │ WebSocket(公网)
       ▼                                    ▼
┌───────────────────────────────────────────────┐
│  Server（NAS / 云服务器，Docker 部署）          │
│  WebSocket 中转 + REST API + token 认证        │
└───────────────────────────────────────────────┘
```

通信协议为平台无关的 JSON（见 [docs/protocol.md](docs/protocol.md)），手表端优先连接局域网内插件，失败或跨网时自动切换云端中转。

## 快速开始

### 环境要求

- .NET 10 SDK（构建 shared/server/plugin）
- Android Studio + Android SDK（API 33+）+ Wear OS 模拟器（构建/运行 wearos）
- ClassIsland 2.x 本体（加载插件调试）

### 构建

```powershell
# 服务端
dotnet build server/RemoteCI.Server

# 插件
dotnet build plugin/RemoteCI.Plugin

# 手表端：用 Android Studio 打开 wearos/ 目录构建
#   或命令行：cd wearos; .\dev.ps1 run（构建+安装+启动到模拟器）
#   模拟器：AVD PixelWatch2_API35（Wear OS 5.1），启动见 docs/platform-notes.md
```

详细部署见 [docs/deployment.md](docs/deployment.md)。

## 开源许可

本项目整体基于 **GPLv3** 授权（见 [LICENSE](LICENSE)）。

- 插件端基于 ClassIsland 2.x 开发：ClassIsland 本体为 GPLv3，`ClassIsland.PluginSdk` 等 SDK 库为 LGPLv3，本项目插件按 GPLv3 发布以满足上架 ClassIsland 插件市场的要求。
- 第三方依赖清单与版权声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
