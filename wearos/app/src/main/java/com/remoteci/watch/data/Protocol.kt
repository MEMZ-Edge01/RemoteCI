package com.remoteci.watch.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

/**
 * 协议常量与数据模型。
 * 与仓库 shared/RemoteCI.Shared（C#）严格对齐：
 * 消息类型、枚举数值、JSON 字段名（小驼峰）都必须保持一致。
 */
object Protocol {
    const val VERSION = 1

    const val TYPE_STATE_PUSH = "state_push"
    const val TYPE_EVENT_NOTIFY = "event_notify"
    const val TYPE_COMMAND = "command"

    // ClassStateKind（与 C# 枚举数值对齐）
    const val STATE_NONE = 0
    const val STATE_CLASS = 1
    const val STATE_BREAKING = 2
    const val STATE_AFTER_SCHOOL = 3

    // ClassEventKind
    const val EVENT_ON_CLASS = 1
    const val EVENT_ON_BREAKING = 2
    const val EVENT_AFTER_SCHOOL = 3
    const val EVENT_STATE_CHANGED = 4

    // CommandKind
    const val CMD_SWITCH_WEEK = 1
    const val CMD_TEMP_SWAP = 2
}

/** WebSocket 消息统一信封。 */
@Serializable
data class Envelope(
    @SerialName("protocolVersion") val protocolVersion: Int = Protocol.VERSION,
    val type: String,
    @SerialName("messageId") val messageId: String = "",
    val timestamp: String = "",
    val sender: String? = null,
    val payload: JsonElement? = null,
)

/** 课表状态快照（state_push 载荷）。 */
@Serializable
data class ClassStateSnapshot(
    @SerialName("currentSubject") val currentSubject: String? = null,
    @SerialName("nextClassSubject") val nextClassSubject: String? = null,
    @SerialName("currentState") val currentState: Int = Protocol.STATE_NONE,
    @SerialName("currentTimeLayoutItem") val currentTimeLayoutItem: String? = null,
    @SerialName("nextClassTimeLayoutItem") val nextClassTimeLayoutItem: String? = null,
    @SerialName("classPlanName") val classPlanName: String? = null,
    @SerialName("weekRotation") val weekRotation: Int? = null,
    @SerialName("isClassPlanEnabled") val isClassPlanEnabled: Boolean = false,
    @SerialName("isClassPlanLoaded") val isClassPlanLoaded: Boolean = false,
    @SerialName("onClassLeftTime") val onClassLeftTime: String? = null,
    @SerialName("onBreakingLeftTime") val onBreakingLeftTime: String? = null,
    @SerialName("lessonConfirmed") val lessonConfirmed: Boolean = false,
    @SerialName("generatedAt") val generatedAt: String? = null,
)

/** 课程事件（event_notify 载荷），用于手表通知+振动。 */
@Serializable
data class ClassEvent(
    val event: Int,
    val subject: String? = null,
    val message: String? = null,
    @SerialName("occurredAt") val occurredAt: String? = null,
)

/** 控制指令（command 载荷）。 */
@Serializable
data class CommandMessage(
    val command: Int,
    val parameters: Map<String, JsonElement> = emptyMap(),
    val result: CommandResult? = null,
)

/** 指令执行结果。 */
@Serializable
data class CommandResult(
    val success: Boolean = false,
    val message: String? = null,
)

/** 云端配对请求/响应。 */
@Serializable
data class PairRequest(
    @SerialName("pairCode") val pairCode: String,
    val role: String,
)

@Serializable
data class PairResponse(
    val token: String,
    val role: String,
    @SerialName("expiresAt") val expiresAt: String? = null,
)
