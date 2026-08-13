# RemoteCI fnOS 应用包

本目录是飞牛 fnOS 应用（`.fpk`）工程，把 RemoteCI 服务端以 Docker 应用形式打包，可直接在飞牛 fnOS 应用中心安装。

## 结构

| 路径 | 说明 |
| --- | --- |
| `manifest` | 应用包描述：应用名、版本、平台、桌面入口、服务端口 |
| `config/privilege` | 运行用户声明（Docker 应用由容器承载进程，保持 package 最小权限） |
| `config/resource` | Docker 项目资源声明 |
| `app/docker/docker-compose.yaml` | 容器编排：镜像、端口、数据卷、环境变量 |
| `app/ui/config` | 桌面入口（打开 WebUI） |
| `cmd/` | 生命周期脚本；start/stop 由应用中心接管，status 检查容器状态 |
| `wizard/install` | 安装向导：Web 端口、管理员密码、插件配对码 |
| `ICON.PNG` / `ICON_256.PNG` | 包图标（64×64 / 256×256） |

## 构建

1. 下载 [fnpack](https://developer.fnnas.com/docs/cli/fnpack/)（linux-amd64）到 `.tools/fnpack` 并授权：

   ```bash
   mkdir -p .tools
   curl -fsSL -o .tools/fnpack https://static2.fnnas.com/fnpack/fnpack-1.2.3-linux-amd64
   chmod +x .tools/fnpack
   ```

2. 打包（版本号从最近的 git tag 读取，也可以用参数覆盖）：

   ```bash
   bash fnos/build.sh          # 使用最近 git tag
   bash fnos/build.sh 0.3.1    # 指定版本
   ```

产物输出到 `artifacts/release/RemoteCI-<版本>.fpk`。GitHub Actions 的 `release.yml` 会在推送 `v*` 标签时自动完成：构建并推送多架构镜像到 GHCR → 打包 fpk → 附加到 GitHub Release。

## 安装与更新

1. 从 GitHub Releases 下载 `RemoteCI-<版本>.fpk`。
2. 在飞牛 fnOS 应用中心选择"手动安装"，按向导填写端口、管理员密码和插件配对码。
3. 安装后从桌面入口打开 RemoteCI WebUI。
4. 后续版本检查与升级由 fnOS 应用商店统一管理；WebUI 的“系统更新”面板仅显示“由fnOS应用商店管理”，不在容器内下载或安装 fpk。

数据（SQLite）保存在 `/var/apps/remoteci/var/data`，卸载应用不会删除它。
