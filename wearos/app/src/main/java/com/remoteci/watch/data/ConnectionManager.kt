package com.remoteci.watch.data

import android.content.Context
import android.os.Build
import android.util.Base64
import java.io.IOException
import java.security.MessageDigest
import java.time.Duration
import java.time.OffsetDateTime
import java.util.UUID
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.coroutines.resume
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/** 云端和局域网共用的认证、状态与命令入口。 */
object ConnectionManager {
    sealed interface State {
        data object Idle : State
        data object Connecting : State
        data object LanConnected : State
        data object CloudConnected : State
        data class Error(val message: String) : State
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(6, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()
    private lateinit var sessions: SecureSessionStore
    private var webSocket: WebSocket? = null
    private var activeJob: Job? = null
    private var refreshJob: Job? = null
    private var volumeJob: Job? = null
    private var desiredSettings: WatchSettings? = null
    private var accessToken: String? = null
    private var generation = 0

    val state = MutableStateFlow<State>(State.Idle)
    val currentUser = MutableStateFlow<UserProfile?>(null)
    val snapshot = MutableStateFlow<ClassStateSnapshot?>(null)
    val schedule = MutableStateFlow<ScheduleBundle?>(null)
    val events = MutableSharedFlow<ClassEvent>(extraBufferCapacity = 32)
    val lastCommandResult = MutableStateFlow<CommandResult?>(null)

    fun initialize(context: Context) {
        if (!::sessions.isInitialized) sessions = SecureSessionStore(context.applicationContext)
    }

    fun hasSavedSession(): Boolean = ::sessions.isInitialized && sessions.load() != null

    /** password 仅用于本次 HTTPS 登录；成功后只保存 Keystore 加密的设备会话密钥。 */
    fun connect(settings: WatchSettings, password: String? = null) {
        check(::sessions.isInitialized) { "ConnectionManager 尚未初始化" }
        generation++
        val attempt = generation
        desiredSettings = settings
        activeJob?.cancel()
        refreshJob?.cancel()
        volumeJob?.cancel()
        webSocket?.close(1000, "switch")
        webSocket = null
        state.value = State.Connecting
        lastCommandResult.value = null

        activeJob = scope.launch {
            try {
                if (!password.isNullOrEmpty()) {
                    if (!settings.cloudConnectionEnabled) throw IOException("首次登录必须启用云服务器")
                    val auth = loginCloud(settings, password)
                    persist(auth)
                    connectCloud(settings, auth, attempt)
                    return@launch
                }

                val saved = sessions.load() ?: throw MissingSessionException()
                if (settings.username.isNotBlank() && saved.username != settings.username)
                    throw MissingSessionException()

                val lanOk = settings.lanConnectionEnabled && settings.lanHost.isNotBlank() &&
                    connectLan(settings, saved, attempt)
                if (lanOk) return@launch
                if (!settings.cloudConnectionEnabled) throw IOException("局域网连接失败")
                val auth = refreshCloud(settings, saved)
                persist(auth)
                connectCloud(settings, auth, attempt)
            } catch (_: MissingSessionException) {
                state.value = State.Error("请先使用账号密码登录")
                currentUser.value = null
            } catch (error: Exception) {
                state.value = State.Error(error.message ?: "连接失败")
            }
        }
    }

    fun disconnect(clearUser: Boolean = false) {
        generation++
        desiredSettings = null
        activeJob?.cancel()
        refreshJob?.cancel()
        volumeJob?.cancel()
        webSocket?.close(1000, "disconnect")
        webSocket = null
        accessToken = null
        if (clearUser) currentUser.value = null
        state.value = State.Idle
    }

    fun logout(settings: WatchSettings) {
        val token = accessToken
        val saved = sessions.load()
        if (token != null && saved != null) {
            scope.launch {
                runCatching {
                    okHttp.newCall(
                        Request.Builder()
                            .url("${settings.cloudServerUrl.trimEnd('/')}/api/auth/logout")
                            .header("Authorization", "Bearer $token")
                            .post(ByteArray(0).toRequestBody(null))
                            .build(),
                    ).execute().close()
                }
            }
        }
        sessions.clear()
        disconnect(clearUser = true)
    }

    fun sendScheduleChange(request: ScheduleChangeRequest) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_CHANGE_SCHEDULE, scheduleChange = request),
            Protocol.PERMISSION_MANAGE_SCHEDULE,
        )
    }

    fun sendNotification(
        title: String,
        message: String,
        isNotificationEffectEnabled: Boolean,
        isNotificationSoundEnabled: Boolean,
        isSpeechEnabled: Boolean,
    ) {
        sendCommand(
            CommandMessage(
                command = Protocol.CMD_SEND_NOTIFICATION,
                notification = NotificationRequest(
                    title = title.trim(),
                    message = message.trim(),
                    isNotificationEffectEnabled = isNotificationEffectEnabled,
                    isNotificationSoundEnabled = isNotificationSoundEnabled,
                    isSpeechEnabled = isSpeechEnabled,
                ),
            ),
            Protocol.PERMISSION_SEND_NOTIFICATIONS,
        )
    }

    fun clearNotifications() {
        sendCommand(
            CommandMessage(command = Protocol.CMD_CLEAR_NOTIFICATIONS),
            Protocol.PERMISSION_SEND_NOTIFICATIONS,
        )
    }

    fun setMainMenuVisible(visible: Boolean) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_SET_MAIN_MENU_VISIBILITY, mainMenuVisible = visible),
            Protocol.PERMISSION_SYSTEM_CONTROL,
        )
    }

    fun sendPowerAction(action: Int) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_POWER, powerAction = action),
            Protocol.PERMISSION_SYSTEM_CONTROL,
        )
    }

    fun setVolume(level: Int) {
        // 表冠会高频产生事件，只发送短时间内的最后一个值，避免淹没 WebSocket 命令队列。
        volumeJob?.cancel()
        volumeJob = scope.launch {
            delay(80)
            sendCommand(
                CommandMessage(
                    command = Protocol.CMD_VOLUME,
                    volume = VolumeControlRequest(level = level.coerceIn(0, 100)),
                ),
                Protocol.PERMISSION_SYSTEM_CONTROL,
            )
        }
    }

    fun setMuted(muted: Boolean) {
        sendCommand(
            CommandMessage(command = Protocol.CMD_VOLUME, volume = VolumeControlRequest(muted = muted)),
            Protocol.PERMISSION_SYSTEM_CONTROL,
        )
    }

    private fun sendCommand(command: CommandMessage, requiredPermission: Int) {
        if (currentUser.value?.has(requiredPermission) != true) {
            lastCommandResult.value = CommandResult(false, "FORBIDDEN", "权限不足")
            return
        }
        lastCommandResult.value = null
        sendEnvelope(
            Envelope(
                type = Protocol.TYPE_COMMAND,
                messageId = newMessageId(),
                payload = json.encodeToJsonElement(CommandMessage.serializer(), command),
            ),
        )
    }

    private suspend fun connectLan(
        settings: WatchSettings,
        session: PersistedDeviceSession,
        attempt: Int,
    ): Boolean = connectWebSocket(
        url = "ws://${settings.lanHost}:${settings.lanPort}/ws",
        successState = State.LanConnected,
        session = session,
        attempt = attempt,
    )

    private suspend fun connectCloud(settings: WatchSettings, auth: AuthResponse, attempt: Int) {
        accessToken = auth.accessToken
        val schemeUrl = settings.cloudServerUrl.trimEnd('/')
            .replaceFirst("https://", "wss://")
            .replaceFirst("http://", "ws://")
        if (!connectWebSocket(
                "$schemeUrl/ws?token=${auth.accessToken}",
                State.CloudConnected,
                null,
                attempt,
            )
        ) throw IOException("云端连接失败")
        scheduleAccessRefresh(settings, auth.accessExpiresAt, attempt)
    }

    private suspend fun loginCloud(settings: WatchSettings, password: String): AuthResponse =
        postAuth(
            settings,
            "/api/auth/login",
            json.encodeToString(LoginRequest(settings.username.trim(), password, "${Build.MANUFACTURER} ${Build.MODEL}")),
        )

    private suspend fun refreshCloud(settings: WatchSettings, session: PersistedDeviceSession): AuthResponse =
        try {
            postAuth(
                settings,
                "/api/auth/refresh",
                json.encodeToString(RefreshSessionRequest(session.deviceSessionId, session.deviceSecret)),
            )
        } catch (error: AuthenticationException) {
            sessions.clear()
            throw MissingSessionException()
        }

    private suspend fun postAuth(settings: WatchSettings, path: String, bodyJson: String): AuthResponse =
        withContext(Dispatchers.IO) {
            val request = Request.Builder()
                .url("${settings.cloudServerUrl.trimEnd('/')}$path")
                .post(bodyJson.toRequestBody("application/json".toMediaType()))
                .build()
            okHttp.newCall(request).execute().use { response ->
                if (response.code == 401) throw AuthenticationException()
                if (!response.isSuccessful) throw IOException("登录服务返回 HTTP ${response.code}")
                json.decodeFromString(AuthResponse.serializer(), response.body.string())
            }
        }

    private fun persist(auth: AuthResponse) {
        sessions.save(
            PersistedDeviceSession(
                username = auth.user.username,
                deviceSessionId = auth.deviceSessionId,
                deviceSecret = auth.deviceSecret,
                deviceExpiresAt = auth.deviceExpiresAt,
            ),
        )
        currentUser.value = auth.user
    }

    private suspend fun connectWebSocket(
        url: String,
        successState: State,
        session: PersistedDeviceSession?,
        attempt: Int,
    ): Boolean = suspendCancellableCoroutine { continuation ->
        val listener = object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (attempt != generation) webSocket.close(1000, "superseded") else ConnectionManager.webSocket = webSocket
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val envelope = runCatching { json.decodeFromString(Envelope.serializer(), text) }.getOrNull() ?: return
                if (envelope.protocolVersion != Protocol.VERSION) {
                    state.value = State.Error("协议版本不兼容，需要 v${Protocol.VERSION}")
                    webSocket.close(1008, "protocol")
                    if (continuation.isActive) continuation.resume(false)
                    return
                }
                if (envelope.type == Protocol.TYPE_AUTH_CHALLENGE && session != null) {
                    val challenge = envelope.payload?.let {
                        json.decodeFromJsonElement(AuthChallenge.serializer(), it)
                    } ?: return
                    val proof = createProof(challenge, session)
                    webSocket.send(
                        json.encodeToString(
                            Envelope.serializer(),
                            Envelope(
                                type = Protocol.TYPE_AUTH_PROOF,
                                messageId = newMessageId(),
                                payload = json.encodeToJsonElement(AuthProof.serializer(), proof),
                            ),
                        ),
                    )
                    return
                }

                val auth = handleEnvelope(envelope)
                if (auth != null && continuation.isActive) {
                    if (auth.authenticated && auth.user != null) {
                        state.value = successState
                        continuation.resume(true)
                    } else {
                        webSocket.close(1008, "auth failed")
                        continuation.resume(false)
                    }
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (continuation.isActive) continuation.resume(false)
                if (ConnectionManager.webSocket === webSocket) ConnectionManager.webSocket = null
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (ConnectionManager.webSocket === webSocket) ConnectionManager.webSocket = null
                if (attempt == generation && !continuation.isActive) scheduleReconnect(attempt)
            }
        }
        val socket = okHttp.newWebSocket(Request.Builder().url(url).build(), listener)
        continuation.invokeOnCancellation { socket.close(1000, "cancelled") }
    }

    private fun handleEnvelope(envelope: Envelope): AuthState? = when (envelope.type) {
        Protocol.TYPE_AUTH_STATE -> envelope.payload?.let {
            json.decodeFromJsonElement(AuthState.serializer(), it).also { auth ->
                currentUser.value = if (auth.authenticated) auth.user else null
                if (!auth.authenticated) state.value = State.Error(auth.error ?: "登录已失效")
            }
        }
        Protocol.TYPE_STATE_PUSH -> {
            envelope.payload?.let { snapshot.value = json.decodeFromJsonElement(ClassStateSnapshot.serializer(), it) }
            null
        }
        Protocol.TYPE_SCHEDULE_SYNC -> {
            envelope.payload?.let { schedule.value = json.decodeFromJsonElement(ScheduleBundle.serializer(), it) }
            null
        }
        Protocol.TYPE_EVENT_NOTIFY -> {
            envelope.payload?.let { events.tryEmit(json.decodeFromJsonElement(ClassEvent.serializer(), it)) }
            null
        }
        Protocol.TYPE_COMMAND_RESULT -> {
            envelope.payload?.let { lastCommandResult.value = json.decodeFromJsonElement(CommandResult.serializer(), it) }
            null
        }
        else -> null
    }

    private fun createProof(challenge: AuthChallenge, session: PersistedDeviceSession): AuthProof {
        val clientNonce = Base64.encodeToString(java.security.SecureRandom().generateSeed(24), Base64.NO_WRAP)
        val verifier = MessageDigest.getInstance("SHA-256").digest(session.deviceSecret.encodeToByteArray())
        val canonical = "${Protocol.VERSION}|${challenge.challengeId}|${challenge.nonce}|$clientNonce|${session.deviceSessionId.replace("-", "").lowercase()}"
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(verifier, "HmacSHA256")) }
        return AuthProof(
            challengeId = challenge.challengeId,
            deviceSessionId = session.deviceSessionId,
            clientNonce = clientNonce,
            proof = Base64.encodeToString(mac.doFinal(canonical.encodeToByteArray()), Base64.NO_WRAP),
        )
    }

    private fun scheduleAccessRefresh(settings: WatchSettings, expiresAt: String, attempt: Int) {
        refreshJob?.cancel()
        val delayMillis = runCatching {
            (Duration.between(OffsetDateTime.now(), OffsetDateTime.parse(expiresAt)).toMillis() - 60_000)
                .coerceAtLeast(30_000)
        }.getOrDefault(50 * 60_000L)
        refreshJob = scope.launch {
            delay(delayMillis)
            if (attempt == generation) connect(settings)
        }
    }

    private fun scheduleReconnect(attempt: Int) {
        val settings = desiredSettings ?: return
        state.value = State.Connecting
        scope.launch {
            delay(5_000)
            if (attempt == generation) connect(settings)
        }
    }

    private fun sendEnvelope(envelope: Envelope) {
        webSocket?.send(json.encodeToString(Envelope.serializer(), envelope))
            ?: run { lastCommandResult.value = CommandResult(false, "OFFLINE", "当前未连接") }
    }

    private fun newMessageId(): String = UUID.randomUUID().toString().replace("-", "")
    private class MissingSessionException : Exception()
    private class AuthenticationException : Exception()
}
