# RemoteCI 协议 v2

协议 v2 是三端同时升级的破坏性版本。所有 WebSocket 信封都带 `protocolVersion: 2`、`type`、`messageId`、可选 `replyToMessageId`、时间和 `payload`。v1 客户端会收到 `PROTOCOL_VERSION_UNSUPPORTED`，共享配对码登录不再受理。

## 身份与权限

有效权限是位掩码：

| 值 | 权限 |
| ---: | --- |
| 1 | `ViewCurrentCourse` |
| 2 | `AccessWebUi` |
| 4 | `ManageUsers` |
| 8 | `SendNotifications` |
| 16 | `ManageSchedule` |
| 32 | `SystemControl` |

管理员的有效权限固定为 63。普通用户固定包含值 1，其余五项来自服务端授权。`SystemControl` 单独保护主界面显隐和 Windows 电源操作，不与发送通知权限混用。

账号密码只出现在第一次 HTTPS `POST /api/auth/login` 的请求内。响应包含 1 小时 `accessToken`、30 天 `deviceSessionId/deviceSecret` 和用户有效权限。`POST /api/auth/refresh` 会同时轮换访问令牌和设备密钥；旧值立即失效。

## 局域网挑战认证

插件建立连接后发送 `auth_challenge`：

```json
{"challengeId":"...","nonce":"base64","expiresAt":"..."}
```

手表计算：

1. `verifier = SHA256(UTF8(deviceSecret))`。
2. 生成随机 `clientNonce`。
3. 构造 UTF-8 文本 `2|challengeId|nonce|clientNonce|deviceSessionIdWithoutHyphensLowercase`。
4. `proof = Base64(HMAC-SHA256(verifier, canonicalText))`。
5. 发送 `auth_proof`，只包含挑战号、设备会话 ID、客户端随机数和证明。

插件只保存服务器同步的 `verifier`，挑战有效期 30 秒且先消费后校验，因此失败和成功请求都不能重放。授权镜像超过 24 小时仍可认证查看课程，但有效权限被收缩为 `ViewCurrentCourse`。

## WebSocket 消息

| 类型 | 方向 | 载荷 |
| --- | --- | --- |
| `auth_challenge` | 插件 → 手表 | `AuthChallenge` |
| `auth_proof` | 手表 → 插件 | `AuthProof` |
| `auth_state` | 接入端 → 手表 | 当前用户与有效权限，或错误码 |
| `account_sync` | 服务端 → 插件 | 账号元数据、有效权限、设备验证器、版本和生成时间 |
| `state_push` | 插件 → 服务端/手表 | 高频当前课程、提醒播放、主界面显隐与可用电源状态，不含完整课表 |
| `schedule_sync` | 插件 → 服务端/手表 | 今天起七天的日期、课程、科目清单和每日修订号 |
| `event_notify` | 插件 → 服务端/手表 | 上课、下课、放学、课表变更、自定义消息、ClassIsland 自动化或第三方插件通知 |
| `command` | 手表/服务端 → 插件 | 结构化换课、通知、主界面或电源命令 |
| `command_result` | 插件 → 发起者 | 真实成功、失败码、消息和可选新修订号 |

`command_result.replyToMessageId` 必须等于请求的 `messageId`。服务端只把结果交给对应的 WebSocket 或等待中的 WebUI 请求，等待上限 15 秒。

`event_notify.payload.event` 的值 6 表示 ClassIsland 自动化“显示提醒”行动产生的通知，值 7 表示第三方 ClassIsland 插件产生的通知。手表分别持久化开关；内置课程、天气等通知不会被值 7 重复转发。

自定义通知由插件最终执行时强制把标题格式化为 `由用户名发送：原标题`。发送者名称取自已认证账号的 `displayName`（界面称“用户名”），而 `username` 是唯一登录 ID；手表、WebUI 和其他客户端均不能覆盖或移除署名前缀。通知请求还可通过 `isNotificationEffectEnabled`、`isNotificationSoundEnabled` 和 `isSpeechEnabled` 分别控制 ClassIsland 的提醒强调特效、提醒音效和语音朗读；省略时均为关闭。

控制命令值 3 为清除当前 ClassIsland 提醒，值 4 通过 `mainMenuVisible` 设置主界面显隐，值 5 通过 `powerAction` 选择关机、重启、睡眠或休眠。值 6 通过 `volume.level` 设置 Windows 默认播放设备的 0-100 主音量，或通过 `volume.muted` 设置静音状态。休眠入口只在插件状态报告 Windows 已启用休眠时显示。

状态快照中的 `isVolumeControlAvailable`、`volumePercent` 和 `isMuted` 分别表示默认播放设备是否可控、当前主音量百分比和静音状态，手表必须以这些真实状态刷新音量页。

## 换课

`ScheduleChangeRequest` 包含：

- `date`：今天起未来七天的 `yyyy-MM-dd`。
- `mode`：1 为交换，2 为替换。
- `sourceIndex`：原节次的零基索引。
- `targetIndex`：交换模式必填。
- `replacementSubjectId`：替换模式必填。
- `expectedRevision`：客户端读取当天课表时的修订号。

插件发现修订号已变化时返回 `SCHEDULE_STALE` 和最新修订号，不覆盖别人刚完成的修改。

## REST API

主要端点：

- `POST /api/plugin/pair`
- `POST /api/auth/login`、`/api/auth/refresh`、`/api/auth/logout`
- `GET /api/me`、`POST /api/me/password`
- `GET/DELETE /api/me/sessions`
- `GET /api/state`、`GET /api/schedule`
- `POST /api/commands`
- `GET/POST/PUT/DELETE /api/users`
- `POST /api/users/{id}/password`
- `POST /api/plugin/pairing-code`
- `GET /api/admin/status`

REST 手表请求使用 `Authorization: Bearer <accessToken>`。Razor WebUI 使用 HttpOnly、SameSite Cookie 和表单防伪令牌，不把令牌放入浏览器存储。
