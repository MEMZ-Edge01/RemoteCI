package com.remoteci.watch.data

import android.content.Context
import android.os.Build
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
import kotlinx.coroutines.CancellationException
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
import kotlinx.serialization.builtins.ListSerializer
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
        // 静默断网（Wi-Fi 掉线、NAT 失效）时 TCP 不会通知应用，依赖 WebSocket ping 主动探测，
        // 否则连接会“看起来还活着”却永远收不到数据、永不重连。
        .pingInterval(20, TimeUnit.SECONDS)
        .build()
    private val lanDiscoveryClient = LanDiscoveryClient(okHttp, json)
    private lateinit var sessions: SecureSessionStore
    private var webSocket: WebSocket? = null
    private var activeJob: Job? = null
    private var refreshJob: Job? = null
    private var volumeJob: Job? = null
    private var discoveryJob: Job? = null
    private var desiredSettings: WatchSettings? = null
    private var accessToken: String? = null
    private var lastLanAdvertisement: PluginNetworkInfo? = null
    // OkHttp 回调线程、主线程与协程会并发读写 generation，必须用原子操作保证 attempt 校验可靠。
    private val generation = java.util.concurrent.atomic.AtomicInteger(0)
    // 断线自动重连的指数退避（连接成功后复位）；避免服务端抖动时 5 秒固定间隔造成重连风暴。
    private var reconnectDelayMs = InitialReconnectDelayMs

    val state = MutableStateFlow<State>(State.Idle)
    /** 当前认证连接所属的 WebUI 版本；未知时禁止手表自行升级。 */
    val serverVersion = MutableStateFlow<String?>(null)
    val currentUser = MutableStateFlow<UserProfile?>(null)
    val snapshot = MutableStateFlow<ClassStateSnapshot?>(null)
    val schedule = MutableStateFlow<ScheduleBundle?>(null)
    val extensions = MutableStateFlow<List<ExtensionDefinition>>(emptyList())
    val settings = MutableStateFlow<SettingsSync?>(null)
    val events = MutableSharedFlow<ClassEvent>(extraBufferCapacity = 32)
    val lastCommandResult = MutableStateFlow<CommandResult?>(null)
    /** 网络发现或成功直连后产生的本地设置更新，由界面层持久化。 */
    val discoveredSettings = MutableSharedFlow<WatchSettings>(extraBufferCapacity = 1)
    val lanPlugins = MutableStateFlow<List<LanPluginCandidate>>(emptyList())
    val lanDiscoveryStatus = MutableStateFlow<String?>(null)
    val lanDiscoveryScanning = MutableStateFlow(false)
    /** 引导返回的云服务器地址与上次使用的不同：等待用户二次确认的候选（TOFU 强阻断）。 */
    val lanBootstrapPending = MutableStateFlow<Pair<LanPluginCandidate, WatchSettings>?>(null)

    fun initialize(context: Context) {
        if (!::sessions.isInitialized) sessions = SecureSessionStore(context.applicationContext)
    }

    fun hasSavedSession(): Boolean = ::sessions.isInitialized && sessions.load() != null

    fun scanLanPlugins() {
        discoveryJob?.cancel()
        lanPlugins.value = emptyList()
        lanDiscoveryStatus.value = "正在扫描同一局域网中的 RemoteCI 插件…"
        lanDiscoveryScanning.value = true
        discoveryJob = scope.launch {
            try {
                val found = lanDiscoveryClient.scan()
                lanPlugins.value = found
                lanDiscoveryStatus.value = if (found.isEmpty())
                    "未发现插件，可检查 Wi-Fi、UDP ${Protocol.LAN_DISCOVERY_PORT} 防火墙或手动填写地址"
                else
                    "发现 ${found.size} 台插件，请选择要连接的电脑"
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                lanDiscoveryStatus.value = "扫描失败：${error.message ?: "网络不可用"}"
            } finally {
                lanDiscoveryScanning.value = false
            }
        }
    }

    suspend fun loadLanBootstrap(
        settings: WatchSettings,
        candidate: LanPluginCandidate,
    ): WatchSettings? {
        lanDiscoveryStatus.value = "正在连接 ${candidate.instanceName}…"
        return try {
            val bootstrap = lanDiscoveryClient.fetchBootstrap(candidate)
            val updated = mergeLanBootstrapInfo(settings, candidate, bootstrap)
            lanPlugins.value = emptyList()
            // 局域网发现与 bootstrap 均无认证：明文 HTTP 提示窃听风险；
            // 与上次实际使用的地址不同时强制二次确认（TOFU），防止伪造引导诱导输入密码。
            val insecure = updated.cloudServerUrl.startsWith("http://")
            val changed = bootstrapUrlChanged(settings.cloudServerUrl, updated.cloudServerUrl)
            if (changed) {
                lanBootstrapPending.value = candidate to updated
                lanDiscoveryStatus.value =
                    "云服务器与上次使用的不同：${updated.cloudServerUrl}。请再次点击确认，否则不要登录" +
                        (if (insecure) "（且为明文 HTTP）" else "")
                return null
            }
            lanBootstrapPending.value = null
            lanDiscoveryStatus.value = "已获取云服务器：${updated.cloudServerUrl}，确认后请点安全登录" +
                if (insecure) "（明文 HTTP，请确认网络可信）" else ""
            updated
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            lanDiscoveryStatus.value = "连接插件失败：${error.message ?: "未返回云服务器信息"}"
            null
        }
    }

    /** 用户对 TOFU 候选二次确认；返回待应用的设置（界面层持久化）。 */
    fun confirmLanBootstrap(): WatchSettings? {
        val pending = lanBootstrapPending.value ?: return null
        lanBootstrapPending.value = null
        val updated = pending.second
        val insecure = updated.cloudServerUrl.startsWith("http://")
        lanDiscoveryStatus.value = "已确认使用 ${updated.cloudServerUrl}，请点安全登录" +
            if (insecure) "（明文 HTTP）" else ""
        return updated
    }

    /** password 仅用于本次 HTTPS 登录；成功后只保存 Keystore 加密的设备会话密钥。 */
    fun connect(settings: WatchSettings, password: String? = null) {
        check(::sessions.isInitialized) { "ConnectionManager 尚未初始化" }
        discoveryJob?.cancel()
        lanDiscoveryScanning.value = false
        val attempt = generation.incrementAndGet()
        desiredSettings = settings
        activeJob?.cancel()
        refreshJob?.cancel()
        volumeJob?.cancel()
        webSocket?.close(1000, "switch")
        webSocket = null
        state.value = State.Connecting
        serverVersion.value = null
        lastCommandResult.value = null
        extensions.value = emptyList()
        // 重新连接后以服务端下发的设置快照为准，未同步前 UI 按默认开启处理。
        this@ConnectionManager.settings.value = null
        val plan = planConnection(settings, password)

        activeJob = scope.launch {
            try {
                if (plan.bootstrapCloudAuthentication) {
                    val auth = loginCloud(settings, password!!)
                    persist(auth)
                    val session = sessions.load() ?: throw MissingSessionException()
                    if (plan.preferLanAfterCloudAuthentication) {
                        // 登录端点会把新设备会话先同步给插件；镜像传播需要时间，
                        // 因此短间隔重试几次 HMAC 挑战，全部失败再回退云端中转。
                        repeat(6) {
                            delay(400)
                            if (connectLan(settings, session, attempt)) return@launch
                        }
                    }
                    if (!plan.allowCloudFallback)
                        throw IOException("云端认证已完成，但局域网连接失败且云端中转已关闭")
                    connectCloud(settings, auth, attempt)
                    return@launch
                }

                val saved = sessions.load() ?: throw MissingSessionException()
                if (settings.username.isNotBlank() && saved.username != settings.username)
                    throw MissingSessionException()

                val lanOk = settings.lanConnectionEnabled && lanEndpointHosts(settings).isNotEmpty() &&
                    connectLan(settings, saved, attempt)
                if (lanOk) return@launch
                if (!plan.allowCloudFallback) throw IOException("局域网连接失败")
                val auth = refreshCloud(settings, saved)
                persist(auth)
                connectCloud(settings, auth, attempt)
            } catch (_: MissingSessionException) {
                state.value = State.Error("请先使用账号密码登录")
                serverVersion.value = null
                currentUser.value = null
            } catch (error: Exception) {
                state.value = State.Error(error.message ?: "连接失败")
                serverVersion.value = null
                // 登录虽成功但连接已失败，残留的用户信息会让界面误判为“在线”。
                currentUser.value = null
            }
        }
    }

    fun disconnect(clearUser: Boolean = false) {
        generation.incrementAndGet()
        desiredSettings = null
        activeJob?.cancel()
        refreshJob?.cancel()
        volumeJob?.cancel()
        webSocket?.close(1000, "disconnect")
        webSocket = null
        serverVersion.value = null
        accessToken = null
        if (clearUser) currentUser.value = null
        extensions.value = emptyList()
        this@ConnectionManager.settings.value = null
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

    /** 请求当前连接对应的插件重新生成课表；云端和局域网使用同一只读协议消息。 */
    fun requestSchedulePull() {
        lastCommandResult.value = null
        sendEnvelope(schedulePullEnvelope())
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

    fun runExtension(extension: ExtensionDefinition, args: Map<String, String?> = emptyMap()) {
        sendCommand(
            CommandMessage(
                command = Protocol.CMD_RUN_EXTENSION,
                extensionId = extension.id,
                extensionArgs = args,
            ),
            extension.requiredPermission,
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
    ): Boolean {
        for (host in lanEndpointHosts(settings)) {
            if (!connectWebSocket(
                    url = lanWebSocketUrl(host, settings.lanPort),
                    successState = State.LanConnected,
                    session = session,
                    attempt = attempt,
                )
            ) continue

            val updated = settings.copy(
                lanHost = host,
                lanHostCandidates = listOf(host) + lanEndpointHosts(settings).filterNot { it == host },
            )
            desiredSettings = updated
            if (updated != settings) discoveredSettings.tryEmit(updated)
            return true
        }
        return false
    }

    private suspend fun connectCloud(settings: WatchSettings, auth: AuthResponse, attempt: Int) {
        accessToken = auth.accessToken
        // 服务端令牌是标准 Base64，含 +/；不编码时 + 会被服务端解码成空格导致 401。
        if (!connectWebSocket(
                cloudWebSocketUrl(settings.cloudServerUrl, auth.accessToken),
                State.CloudConnected,
                null,
                attempt,
            )
        ) throw IOException("云端连接失败")
        // 收到插件网络信息后可能已经切换到新的局域网连接尝试，旧云端协程不得再覆盖刷新任务。
        if (attempt != generation.get()) return
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
        // 只有“认证成功进入工作状态”的连接在断开后才允许自动重连；
        // 连接失败或认证被拒的 socket 由外层流程决定回退，否则每次失败都会调度一次全量重连形成风暴。
        var authenticated = false
        val listener = object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (attempt != generation.get()) webSocket.close(1000, "superseded") else ConnectionManager.webSocket = webSocket
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (attempt != generation.get()) {
                    webSocket.close(1000, "superseded")
                    if (continuation.isActive) continuation.resume(false)
                    return
                }
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
                    val proof = createAuthProof(challenge, session)
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
                        authenticated = true
                        reconnectDelayMs = InitialReconnectDelayMs // 连接成功即复位退避。
                        continuation.resume(true)
                    } else {
                        webSocket.close(1008, "auth failed")
                        continuation.resume(false)
                    }
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (continuation.isActive) continuation.resume(false)
                clearActiveConnection(webSocket)
                // 已建立的连接异常断开：先呈现错误，onClosed 随后调度自动重连。
                if (authenticated && attempt == generation.get()) {
                    state.value = State.Error("连接已断开：${t.message ?: "网络不可用"}")
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                clearActiveConnection(webSocket)
                if (authenticated && attempt == generation.get() && !continuation.isActive) scheduleReconnect(attempt)
            }
        }
        val socket = okHttp.newWebSocket(Request.Builder().url(url).build(), listener)
        continuation.invokeOnCancellation { socket.close(1000, "cancelled") }
    }

    /** 只清理由当前活跃连接持有的版本，避免旧连接的迟到回调清空新连接状态。 */
    private fun clearActiveConnection(socket: WebSocket) {
        if (webSocket !== socket) return
        webSocket = null
        serverVersion.value = null
    }

    private fun handleEnvelope(envelope: Envelope): AuthState? = when (envelope.type) {
        Protocol.TYPE_AUTH_STATE -> envelope.payload?.let {
            json.decodeFromJsonElement(AuthState.serializer(), it).also { auth ->
                currentUser.value = if (auth.authenticated) auth.user else null
                serverVersion.value = if (auth.authenticated) auth.serverVersion else null
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
        Protocol.TYPE_EXTENSIONS_SYNC -> {
            envelope.payload?.let {
                extensions.value = json.decodeFromJsonElement(
                    ListSerializer(ExtensionDefinition.serializer()),
                    it,
                )
            }
            null
        }
        Protocol.TYPE_SETTINGS_SYNC -> {
            envelope.payload?.let { settings.value = json.decodeFromJsonElement(SettingsSync.serializer(), it) }
            null
        }
        Protocol.TYPE_PLUGIN_NETWORK_INFO -> {
            envelope.payload?.let {
                handlePluginNetworkInfo(json.decodeFromJsonElement(PluginNetworkInfo.serializer(), it))
            }
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

    private fun handlePluginNetworkInfo(info: PluginNetworkInfo) {
        val current = desiredSettings ?: return
        val updated = mergePluginNetworkInfo(current, info)
        desiredSettings = updated
        if (updated != current) discoveredSettings.tryEmit(updated)

        // 同一份不可达地址回退到云端后不反复重试；网卡或端口变化时才重新优先直连。
        val isNewAdvertisement = info != lastLanAdvertisement
        lastLanAdvertisement = info
        if (isNewAdvertisement && info.lanServerEnabled && updated.lanConnectionEnabled &&
            lanEndpointHosts(updated).isNotEmpty() && state.value == State.CloudConnected
        ) {
            connect(updated)
        }
    }

    private fun scheduleAccessRefresh(settings: WatchSettings, expiresAt: String, attempt: Int) {
        refreshJob?.cancel()
        val delayMillis = runCatching {
            (Duration.between(OffsetDateTime.now(), OffsetDateTime.parse(expiresAt)).toMillis() - 60_000)
                .coerceAtLeast(30_000)
        }.getOrDefault(50 * 60_000L)
        refreshJob = scope.launch {
            delay(delayMillis)
            if (attempt == generation.get()) connect(settings)
        }
    }

    private fun scheduleReconnect(attempt: Int) {
        val settings = desiredSettings ?: return
        state.value = State.Connecting
        val delayMs = reconnectDelayMs
        reconnectDelayMs = nextReconnectDelay(reconnectDelayMs)
        scope.launch {
            delay(delayMs)
            if (attempt == generation.get()) connect(settings)
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

internal data class ConnectionPlan(
    val bootstrapCloudAuthentication: Boolean,
    val preferLanAfterCloudAuthentication: Boolean,
    val allowCloudFallback: Boolean,
)

internal const val InitialReconnectDelayMs = 5_000L
internal const val MaxReconnectDelayMs = 60_000L

/** 断线重连的指数退避：每次翻倍并封顶 [MaxReconnectDelayMs]；连接成功后调用方复位为 [InitialReconnectDelayMs]。 */
internal fun nextReconnectDelay(currentMs: Long): Long = (currentMs * 2).coerceAtMost(MaxReconnectDelayMs)

/**
 * 依据 protocol.md 构造局域网 HMAC 挑战证明：
 * 密钥 = SHA-256(deviceSecret)，消息 = `版本|challengeId|nonce|clientNonce|无横线小写 sessionId`。
 * [clientNonceBytes] 默认取 24 字节安全随机数，测试可注入固定值复现向量。
 */
internal fun createAuthProof(
    challenge: AuthChallenge,
    session: PersistedDeviceSession,
    clientNonceBytes: ByteArray = java.security.SecureRandom().generateSeed(24),
): AuthProof {
    val clientNonce = java.util.Base64.getEncoder().encodeToString(clientNonceBytes)
    val verifier = MessageDigest.getInstance("SHA-256").digest(session.deviceSecret.encodeToByteArray())
    val canonical = "${Protocol.VERSION}|${challenge.challengeId}|${challenge.nonce}|$clientNonce|" +
        session.deviceSessionId.replace("-", "").lowercase()
    val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(verifier, "HmacSHA256")) }
    return AuthProof(
        challengeId = challenge.challengeId,
        deviceSessionId = session.deviceSessionId,
        clientNonce = clientNonce,
        proof = java.util.Base64.getEncoder().encodeToString(mac.doFinal(canonical.encodeToByteArray())),
    )
}

/** 云端 WebSocket 地址；访问令牌必须做 URL 编码，服务端 Base64 令牌中的 + 在查询串里会被解码成空格。 */
internal fun cloudWebSocketUrl(cloudServerUrl: String, accessToken: String): String {
    val schemeUrl = cloudServerUrl.trimEnd('/')
        .replaceFirst("https://", "wss://")
        .replaceFirst("http://", "ws://")
    return "$schemeUrl/ws?token=${java.net.URLEncoder.encode(accessToken, Charsets.UTF_8.name())}"
}

/** 引导返回的云服务器地址与上次实际使用的地址不同（默认开发地址不算“用过”）时提示用户。 */
internal fun bootstrapUrlChanged(previous: String, current: String): Boolean {
    val old = previous.trim().trimEnd('/')
    val fresh = current.trim().trimEnd('/')
    return old.isNotBlank() &&
        !old.startsWith("http://10.0.2.2") &&
        !old.equals(fresh, ignoreCase = true)
}

/** 密码只能由云端验证，因此密码登录始终允许一次云端引导；开发者开关只控制后续连接回退。 */
internal fun planConnection(settings: WatchSettings, password: String?): ConnectionPlan = ConnectionPlan(
    bootstrapCloudAuthentication = !password.isNullOrEmpty(),
    preferLanAfterCloudAuthentication = !password.isNullOrEmpty() && settings.lanConnectionEnabled &&
        lanEndpointHosts(settings).isNotEmpty(),
    allowCloudFallback = settings.cloudConnectionEnabled,
)

internal fun schedulePullEnvelope() = Envelope(
    type = Protocol.TYPE_SCHEDULE_PULL,
    messageId = UUID.randomUUID().toString().replace("-", ""),
)
