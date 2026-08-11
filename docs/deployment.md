# 服务端部署与三端配对

## 1. 准备环境变量

生产环境必须通过 HTTPS/WSS 访问。推荐在 RemoteCI 前放置 Caddy、Nginx、Traefik 或 NAS 自带反向代理，并把外部 HTTPS 转发到容器的 HTTP 8080 端口。

首次部署前创建 `.env`，不要提交到仓库：

```dotenv
REMOTECI_ADMIN_PASSWORD=请替换为至少8位的强密码
REMOTECI_PLUGIN_PAIR_CODE=请替换为一次性随机配对码
```

若不提供，服务端会各生成一个随机值并只在首次启动日志中显示。数据库创建完成后，这些初始值不会再次使用；不要把默认密码写入镜像、Compose 文件或版本库。

## 2. 启动与反向代理

```powershell
docker compose up -d --build
docker compose logs remoteci
```

Compose 只把 `127.0.0.1:8080` 暴露给宿主机，并把 SQLite 挂载到命名卷 `remoteci-data:/app/data`。反向代理必须转发以下内容：

- 普通 HTTP 请求和 Cookie。
- `/ws` 的 WebSocket Upgrade。
- `X-Forwarded-For` 与 `X-Forwarded-Proto`。

生产手表的云端地址必须填写 `https://` URL。局域网 `ws://电脑IP:8765/ws` 只用于同一可信网络内的手表到插件连接。

## 3. 首次登录与账号

1. 打开公开 HTTPS 地址，使用 `admin` 和初始密码登录。
2. 在“个人账号”中修改密码；改密会撤销所有手表设备会话。
3. 在“人员权限”中为每个同学建立唯一 ID、用户名和初始密码；登录时只输入 ID。
4. 管理员拥有全部权限且不可关闭；普通用户默认只看当前课，可按需勾选四项附加权限。
5. 学生登录后可以在个人页面修改自己的密码和撤销设备。

系统拒绝删除、禁用或降级最后一个管理员。

## 4. 插件配对

1. 在 WebUI 概览页生成一次性插件配对码；该码在使用前持续有效，成功配对后立即作废。
2. 在 ClassIsland 的“RemoteCI 设置”填写云端 HTTPS 地址和该配对码。
3. 重启 ClassIsland。
4. 插件用配对码换取长期凭据后会清空本地配对码，随后自动接收账号、权限与设备验证器镜像。

学生不接触插件配对码。原有项目级共享配对码不能用于协议 v2。

## 5. 手表登录

1. 首次打开手表应用，填写个人账号、密码和云端 HTTPS 地址。
2. 登录成功后密码被丢弃，30 天设备密钥由 Android Keystore AES-GCM 加密保存。
3. 后续可优先局域网直连；失败时自动使用设备会话续登云端。
4. 在“连接与消息”中单独开关上课、下课、放学、课表变更和自定义消息。

## 6. 备份与恢复

停服后备份 SQLite 卷最稳妥：

```powershell
docker compose stop remoteci
docker run --rm -v remoteci-data:/data -v ${PWD}:/backup alpine `
  tar czf /backup/remoteci-data-backup.tgz -C /data .
docker compose start remoteci
```

恢复前应先保存现有卷副本，再把备份解压回 `/app/data`。数据库包含账号哈希、权限、插件凭据和设备会话验证器，应按敏感数据管理。

## 7. 升级

协议 v2 要求服务端、插件和手表一起升级。升级前备份 SQLite 卷，部署服务端并确认迁移成功，再更新 CIPX 和手表 APK。插件离线时 WebUI 的换课与通知会立即失败，不会排队补发。
