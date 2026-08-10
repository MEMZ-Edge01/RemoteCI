package com.remoteci.watch.data

import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.encodeToString
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.MediaType.Companion.toMediaType
import kotlin.coroutines.resume

/**
 * 连接管理器：局域网直连优先，失败自动切换云端中转（混合模式）。
 * 单例持有连接状态、最新快照、事件流与指令回执。
 */
object ConnectionManager {

    sealed interface State {
        data object Idle : State
        data object Connecting : State
        data object LanConnected : State
        data object CloudConnected : State
        data class Error(val message: String) : State
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val json = Json { ignoreUnknownKeys = true }
    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()

    private var webSocket: WebSocket? = null
    private var activeJob: Job? = null

    /** 连接状态（UI 展示/调试）。 */
    val state = MutableStateFlow<State>(State.Idle)

    /** 最新课表快照。 */
    val snapshot = MutableStateFlow<ClassStateSnapshot?>(null)

    /** 课程事件流（触发通知+振动）。 */
    val events = MutableSharedFlow<ClassEvent>(extraBufferCapacity = 16)

    /** 最近一次控制指令回执。 */
    val lastCommandResult = MutableStateFlow<CommandResult?>(null)

    /** 发起连接：先局域网，失败切云端。 */
    fun connect(settings: WatchSettings) {
        activeJob?.cancel()
        webSocket?.close(1000, "switch")
        webSocket = null
        state.value = State.Connecting
        activeJob = scope.launch {
            val lanOk = settings.lanHost.isNotBlank() && tryConnectLan(settings)
            if (!lanOk) {
                tryConnectCloud(settings)
            }
        }
    }

    fun disconnect() {
        activeJob?.cancel()
        webSocket?.close(1000, "disconnect")
        webSocket = null
        state.value = State.Idle
    }

    /** 发送控制指令（切换周次/临时换课），参数为原语键值对。 */
    fun sendCommand(command: Int, vararg pairs: Pair<String, Any>) {
        val params = buildJsonObject {
            pairs.forEach { (key, value) ->
                put(key, value.toJsonElement())
            }
        }
        sendEnvelope(
            Envelope(
                type = Protocol.TYPE_COMMAND,
                payload = buildJsonObject {
                    put("command", command)
                    put("parameters", params)
                },
            ),
        )
    }

    /** 原语 → JsonElement，避免对任意类型反射序列化。 */
    private fun Any.toJsonElement(): JsonElement = when (this) {
        is Int -> JsonPrimitive(this)
        is String -> JsonPrimitive(this)
        is Boolean -> JsonPrimitive(this)
        is Double -> JsonPrimitive(this)
        else -> JsonPrimitive(toString())
    }

    private fun sendEnvelope(envelope: Envelope) {
        webSocket?.send(json.encodeToString(Envelope.serializer(), envelope))
    }

    private suspend fun tryConnectLan(settings: WatchSettings): Boolean {
        val url = "ws://${settings.lanHost}:${settings.lanPort}/ws/${settings.pairCode}"
        return connectWebSocket(url, State.LanConnected)
    }

    private suspend fun tryConnectCloud(settings: WatchSettings) {
        try {
            val token = pairWithCloud(settings)
            val url = "${settings.cloudServerUrl.trimEnd('/')}/ws?token=$token"
            val ok = connectWebSocket(url, State.CloudConnected)
            if (!ok) {
                state.value = State.Error("云端连接失败")
            }
        } catch (e: Exception) {
            state.value = State.Error("云端配对失败：${e.message ?: e.javaClass.simpleName}")
        }
    }

    /** 经 REST /api/pair 用配对码换取云端 token。 */
    private suspend fun pairWithCloud(settings: WatchSettings): String = withContext(Dispatchers.IO) {
        val body = json.encodeToString(
            PairRequest.serializer(),
            PairRequest(pairCode = settings.pairCode, role = "watch"),
        ).toRequestBody("application/json".toMediaType())
        val request = Request.Builder()
            .url("${settings.cloudServerUrl.trimEnd('/')}/api/pair")
            .post(body)
            .build()
        okHttp.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                throw IOException("配对失败 HTTP ${response.code}")
            }
            json.decodeFromString(PairResponse.serializer(), response.body?.string().orEmpty()).token
        }
    }

    /**
     * 建立 WebSocket 连接；成功返回 true。
     * 使用 suspendCancellableCoroutine 将 OkHttp 回调桥接为挂起函数。
     */
    private suspend fun connectWebSocket(url: String, successState: State): Boolean =
        suspendCancellableCoroutine { cont ->
            val listener = object : WebSocketListener() {
                override fun onOpen(ws: WebSocket, response: okhttp3.Response) {
                    webSocket = ws
                    state.value = successState
                    if (cont.isActive) cont.resume(true)
                }

                override fun onMessage(ws: WebSocket, text: String) {
                    handleMessage(text)
                }

                override fun onFailure(ws: WebSocket, t: Throwable, response: okhttp3.Response?) {
                    if (cont.isActive) {
                        state.value = State.Error("连接失败：${t.message ?: "未知错误"}")
                        cont.resume(false)
                    }
                }

                override fun onClosing(ws: WebSocket, code: Int, reason: String) {
                    ws.close(code, reason)
                }

                override fun onClosed(ws: WebSocket, code: Int, reason: String) {
                    if (state.value !is State.Connecting) {
                        state.value = State.Idle
                    }
                }
            }
            okHttp.newWebSocket(Request.Builder().url(url).build(), listener)
            cont.invokeOnCancellation { webSocket?.close(1000, "cancelled") }
        }

    /** 按消息类型分发：快照/事件/回执。 */
    private fun handleMessage(text: String) {
        runCatching {
            val envelope = json.decodeFromString(Envelope.serializer(), text)
            when (envelope.type) {
                Protocol.TYPE_STATE_PUSH -> envelope.payload?.let {
                    snapshot.value = json.decodeFromJsonElement(ClassStateSnapshot.serializer(), it)
                }
                Protocol.TYPE_EVENT_NOTIFY -> envelope.payload?.let {
                    events.tryEmit(json.decodeFromJsonElement(ClassEvent.serializer(), it))
                }
                Protocol.TYPE_COMMAND -> envelope.payload?.let {
                    lastCommandResult.value =
                        json.decodeFromJsonElement(CommandMessage.serializer(), it).result
                }
            }
        }
    }
}
