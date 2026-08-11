package com.remoteci.watch.ui

import android.content.Context
import androidx.activity.compose.BackHandler
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import com.remoteci.watch.data.ConnectionManager
import com.remoteci.watch.data.EventHistory
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.ScheduleChangeRequest
import com.remoteci.watch.data.SettingsStore
import com.remoteci.watch.data.SnapshotStore
import com.remoteci.watch.notif.NotificationHelper
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private enum class Screen {
    Login,
    Home,
    ScheduleOverview,
    ScheduleDatePicker,
    DayPicker,
    Swap,
    LessonPicker,
    SubjectPicker,
    Control,
    Notification,
    Power,
    Volume,
    Settings,
    ConnectionSettings,
    NotificationSettings,
    Update,
}

@Composable
fun RemoteCiApp(context: Context) {
    val settingsStore = remember { SettingsStore(context) }
    val snapshotStore = remember { SnapshotStore(context) }
    val eventHistory = remember { EventHistory(context) }
    val currentVersion = remember {
        runCatching {
            context.packageManager.getPackageInfo(context.packageName, 0).versionName
        }.getOrNull() ?: "0.0.0"
    }
    ConnectionManager.initialize(context)
    val scope = rememberCoroutineScope()

    var settings by remember { mutableStateOf(settingsStore.load()) }
    var screen by rememberSaveable {
        mutableStateOf(if (ConnectionManager.hasSavedSession()) Screen.Home else Screen.Login)
    }
    var password by rememberSaveable { mutableStateOf("") }
    var selectedDate by rememberSaveable { mutableStateOf<String?>(null) }
    var swapMode by rememberSaveable { mutableStateOf(SwapMode.Exchange) }
    var pickerTarget by rememberSaveable { mutableStateOf(LessonTarget.Source) }
    var sourceIndex by rememberSaveable { mutableStateOf<Int?>(null) }
    var targetIndex by rememberSaveable { mutableStateOf<Int?>(null) }
    var replacementSubjectId by rememberSaveable { mutableStateOf<String?>(null) }
    var noticeTitle by rememberSaveable { mutableStateOf("") }
    var noticeMessage by rememberSaveable { mutableStateOf("") }
    var noticeEffectEnabled by rememberSaveable { mutableStateOf(false) }
    var noticeSoundEnabled by rememberSaveable { mutableStateOf(false) }
    var noticeSpeechEnabled by rememberSaveable { mutableStateOf(false) }
    var cachedSnapshot by remember { mutableStateOf(snapshotStore.load()) }
    var cachedSchedule by remember { mutableStateOf(snapshotStore.loadSchedule()) }

    val connectionState by ConnectionManager.state.collectAsState()
    val currentUser by ConnectionManager.currentUser.collectAsState()
    val liveSnapshot by ConnectionManager.snapshot.collectAsState()
    val liveSchedule by ConnectionManager.schedule.collectAsState()
    val commandResult by ConnectionManager.lastCommandResult.collectAsState()
    val displayedSnapshot = liveSnapshot ?: cachedSnapshot
    val displayedSchedule = liveSchedule ?: cachedSchedule
    val currentSettings by rememberUpdatedState(settings)
    val afterSchool = displayedSnapshot?.currentState == Protocol.STATE_AFTER_SCHOOL
    val selectedDay = displayedSchedule?.days?.firstOrNull { it.date == selectedDate }
    val lessons = remember(selectedDay) { buildLessonChoices(selectedDay) }

    LaunchedEffect(Unit) {
        if (ConnectionManager.hasSavedSession()) ConnectionManager.connect(settings)
        ConnectionManager.events.collect { event ->
            NotificationHelper.handle(context, event, currentSettings, eventHistory)
        }
    }
    LaunchedEffect(liveSnapshot) {
        liveSnapshot?.let { snapshotStore.save(it); cachedSnapshot = it }
    }
    LaunchedEffect(liveSchedule) {
        liveSchedule?.let { snapshotStore.saveSchedule(it); cachedSchedule = it }
    }
    LaunchedEffect(currentUser) {
        val user = currentUser
        if (user != null && screen == Screen.Login) screen = Screen.Home
        if (user != null && !user.has(Protocol.PERMISSION_MANAGE_SCHEDULE) &&
            screen in listOf(
                Screen.ScheduleOverview,
                Screen.ScheduleDatePicker,
                Screen.DayPicker,
                Screen.Swap,
                Screen.LessonPicker,
                Screen.SubjectPicker,
            )
        ) screen = Screen.Home
        if (user != null && !user.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) && screen == Screen.Notification)
            screen = Screen.Home
        if (user != null && !user.has(Protocol.PERMISSION_SYSTEM_CONTROL) &&
            screen in listOf(Screen.Power, Screen.Volume))
            screen = Screen.Home
        if (user != null && !user.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) &&
            !user.has(Protocol.PERMISSION_SYSTEM_CONTROL) && screen == Screen.Control)
            screen = Screen.Home
    }
    LaunchedEffect(selectedDay) {
        val enabled = lessons.filter { it.enabled }
        if (enabled.none { it.index == sourceIndex }) sourceIndex = enabled.firstOrNull()?.index
        if (enabled.none { it.index == targetIndex } || targetIndex == sourceIndex)
            targetIndex = enabled.firstOrNull { it.index != sourceIndex }?.index
        if (displayedSchedule?.subjects?.none { it.id == replacementSubjectId } != false)
            replacementSubjectId = displayedSchedule?.subjects?.firstOrNull()?.id
    }

    BackHandler(enabled = screen !in listOf(Screen.Home, Screen.Login)) {
        screen = when (screen) {
            Screen.LessonPicker, Screen.SubjectPicker -> Screen.Swap
            Screen.Swap -> Screen.DayPicker
            Screen.ScheduleDatePicker -> Screen.ScheduleOverview
            Screen.Notification -> Screen.Control
            Screen.Power -> Screen.Control
            Screen.Volume -> Screen.Control
            Screen.ConnectionSettings, Screen.NotificationSettings -> Screen.Settings
            Screen.Update -> Screen.Settings
            else -> Screen.Home
        }
    }

    when (screen) {
        Screen.Login -> LoginScreen(
            settings = settings,
            password = password,
            state = connectionState,
            onSettingsChange = { settings = it },
            onPasswordChange = { password = it },
            onLogin = {
                settingsStore.save(settings)
                ConnectionManager.connect(settings, password)
                password = ""
            },
        )

        Screen.Home -> HomeScreen(
            connectionState = connectionState,
            snapshot = displayedSnapshot,
            user = currentUser,
            onOpenScheduleOverview = {
                selectedDate = initialScheduleDate(displayedSchedule, afterSchool)
                screen = Screen.ScheduleOverview
            },
            onOpenScheduleChange = {
                selectedDate = initialScheduleDate(displayedSchedule, afterSchool)
                screen = Screen.DayPicker
            },
            onOpenNotification = { screen = Screen.Control },
            onOpenSettings = { screen = Screen.Settings },
            onRetryConnection = {
                if (ConnectionManager.hasSavedSession()) ConnectionManager.connect(settings) else screen = Screen.Login
            },
        )

        Screen.ScheduleOverview -> ScheduleOverviewScreen(
            day = selectedDay,
            onPickDate = { screen = Screen.ScheduleDatePicker },
            onBack = { screen = Screen.Home },
        )

        Screen.ScheduleDatePicker -> ScheduleDatePickerScreen(
            bundle = displayedSchedule,
            afterSchool = afterSchool,
            onSelect = { day -> selectedDate = day.date; screen = Screen.ScheduleOverview },
            onBack = { screen = Screen.ScheduleOverview },
        )

        Screen.DayPicker -> DayPickerScreen(
            bundle = displayedSchedule,
            afterSchool = afterSchool,
            onSelect = { day -> selectedDate = day.date; screen = Screen.Swap },
            onBack = { screen = Screen.Home },
        )

        Screen.Swap -> SwapScreen(
            day = selectedDay,
            mode = swapMode,
            sourceLesson = lessons.firstOrNull { it.index == sourceIndex },
            targetLesson = lessons.firstOrNull { it.index == targetIndex },
            replacementSubject = displayedSchedule?.subjects?.firstOrNull { it.id == replacementSubjectId }?.name,
            connectionReady = connectionState is ConnectionManager.State.LanConnected ||
                connectionState is ConnectionManager.State.CloudConnected,
            resultText = commandResult?.let { if (it.success) "已完成：${it.message}" else "失败：${it.message}" },
            onModeChange = { swapMode = it },
            onPickSource = { pickerTarget = LessonTarget.Source; screen = Screen.LessonPicker },
            onPickTarget = {
                screen = if (swapMode == SwapMode.Exchange) {
                    pickerTarget = LessonTarget.Target
                    Screen.LessonPicker
                } else Screen.SubjectPicker
            },
            onSubmit = {
                val day = selectedDay ?: return@SwapScreen
                val source = sourceIndex ?: return@SwapScreen
                ConnectionManager.sendScheduleChange(
                    ScheduleChangeRequest(
                        date = day.date,
                        mode = if (swapMode == SwapMode.Exchange) Protocol.CHANGE_EXCHANGE else Protocol.CHANGE_REPLACE,
                        sourceIndex = source,
                        targetIndex = if (swapMode == SwapMode.Exchange) targetIndex else null,
                        replacementSubjectId = if (swapMode == SwapMode.Replace) replacementSubjectId else null,
                        expectedRevision = day.revision,
                    ),
                )
            },
        )

        Screen.LessonPicker -> LessonPickerScreen(
            title = if (pickerTarget == LessonTarget.Source) "选择原课" else "选择目标课",
            lessons = lessons,
            selectedIndex = if (pickerTarget == LessonTarget.Source) sourceIndex else targetIndex,
            excludedIndex = if (pickerTarget == LessonTarget.Source) targetIndex else sourceIndex,
            onSelect = {
                if (pickerTarget == LessonTarget.Source) sourceIndex = it.index else targetIndex = it.index
                screen = Screen.Swap
            },
        )

        Screen.SubjectPicker -> SubjectPickerScreen(
            subjects = displayedSchedule?.subjects.orEmpty(),
            selectedSubjectId = replacementSubjectId,
            onSelect = { replacementSubjectId = it.id; screen = Screen.Swap },
        )

        Screen.Control -> ControlScreen(
            snapshot = displayedSnapshot,
            user = currentUser,
            resultText = commandResult?.let { if (it.success) "已完成：${it.message}" else "失败：${it.message}" },
            onTeacherComing = {
                // “老师来了”快捷提醒：标题展示“老师来了”，仅开启强调特效，不带音效和语音；
                // 1 秒后自动清除。ClassIsland 先播标题（遮罩）再播正文，因此正文不会在 1 秒内显示，
                // 这里传“老师来了”只是满足服务端正文非空的校验，效果等同正文留空。
                ConnectionManager.sendNotification(
                    title = "老师来了",
                    message = "老师来了",
                    isNotificationEffectEnabled = true,
                    isNotificationSoundEnabled = false,
                    isSpeechEnabled = false,
                )
                scope.launch {
                    delay(1_000)
                    ConnectionManager.clearNotifications()
                }
            },
            onOpenNotification = { screen = Screen.Notification },
            onClearNotifications = ConnectionManager::clearNotifications,
            onToggleMainMenu = {
                ConnectionManager.setMainMenuVisible(!(displayedSnapshot?.isMainMenuVisible ?: true))
            },
            onOpenVolume = { screen = Screen.Volume },
            onOpenPower = { screen = Screen.Power },
            onBack = { screen = Screen.Home },
        )

        Screen.Power -> PowerScreen(
            sleepAvailable = displayedSnapshot?.isSleepAvailable == true,
            hibernateAvailable = displayedSnapshot?.isHibernateAvailable == true,
            onPowerAction = ConnectionManager::sendPowerAction,
            onBack = { screen = Screen.Control },
        )

        Screen.Volume -> VolumeScreen(
            volumePercent = displayedSnapshot?.volumePercent ?: 0,
            muted = displayedSnapshot?.isMuted == true,
            available = displayedSnapshot?.isVolumeControlAvailable == true,
            onVolumeChange = ConnectionManager::setVolume,
            onMutedChange = ConnectionManager::setMuted,
            onBack = { screen = Screen.Control },
        )

        Screen.Notification -> NotificationScreen(
            title = noticeTitle,
            message = noticeMessage,
            effectEnabled = noticeEffectEnabled,
            soundEnabled = noticeSoundEnabled,
            speechEnabled = noticeSpeechEnabled,
            resultText = commandResult?.message,
            onTitleChange = { noticeTitle = it },
            onMessageChange = { noticeMessage = it },
            onEffectEnabledChange = { noticeEffectEnabled = it },
            onSoundEnabledChange = { noticeSoundEnabled = it },
            onSpeechEnabledChange = { noticeSpeechEnabled = it },
            onSend = {
                ConnectionManager.sendNotification(
                    noticeTitle,
                    noticeMessage,
                    noticeEffectEnabled,
                    noticeSoundEnabled,
                    noticeSpeechEnabled,
                )
            },
            onBack = { screen = Screen.Control },
        )

        Screen.Settings -> SettingsScreen(
            onOpenConnection = { screen = Screen.ConnectionSettings },
            onOpenNotifications = { screen = Screen.NotificationSettings },
            onOpenUpdate = { screen = Screen.Update },
            onBack = { screen = Screen.Home },
        )

        Screen.ConnectionSettings -> ConnectionSettingsScreen(
            settings = settings,
            stateText = describeConnection(connectionState),
            onSettingsChange = { settings = it; settingsStore.save(it) },
            onReconnect = { settingsStore.save(settings); ConnectionManager.connect(settings) },
            onLogout = { ConnectionManager.logout(settings); screen = Screen.Login },
            onBack = { screen = Screen.Settings },
        )

        Screen.NotificationSettings -> NotificationSettingsScreen(
            settings = settings,
            onSettingsChange = { settings = it; settingsStore.save(it) },
            onBack = { screen = Screen.Settings },
        )

        Screen.Update -> UpdateScreen(
            context = context,
            currentVersion = currentVersion,
            onBack = { screen = Screen.Settings },
        )
    }
}

private fun describeConnection(state: ConnectionManager.State): String = when (state) {
    ConnectionManager.State.Idle -> "未连接"
    ConnectionManager.State.Connecting -> "连接中…"
    ConnectionManager.State.LanConnected -> "局域网直连"
    ConnectionManager.State.CloudConnected -> "云端中转"
    is ConnectionManager.State.Error -> "错误：${state.message}"
}

internal fun describeClassState(snapshot: com.remoteci.watch.data.ClassStateSnapshot?): String = when (snapshot?.currentState) {
    Protocol.STATE_CLASS -> "上课"
    Protocol.STATE_PREPARE_CLASS -> "准备上课"
    Protocol.STATE_BREAKING -> "下课"
    Protocol.STATE_AFTER_SCHOOL -> "放学"
    else -> "待机"
}
