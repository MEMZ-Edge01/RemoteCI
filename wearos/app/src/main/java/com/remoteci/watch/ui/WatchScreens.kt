package com.remoteci.watch.ui

import android.content.Context
import android.os.SystemClock
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.focusable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.wear.compose.foundation.pager.VerticalPager
import androidx.wear.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.List
import androidx.compose.material.icons.automirrored.rounded.VolumeOff
import androidx.compose.material.icons.automirrored.rounded.VolumeUp
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Code
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.EditNotifications
import androidx.compose.material.icons.rounded.NotificationsOff
import androidx.compose.material.icons.rounded.Palette
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.RestartAlt
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.School
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material.icons.rounded.VisibilityOff
import androidx.compose.material.icons.rounded.SwapHoriz
import androidx.compose.material.icons.rounded.SystemUpdate
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.Slider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.input.rotary.onRotaryScrollEvent
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.wear.compose.foundation.lazy.ScalingLazyColumn
import androidx.wear.compose.foundation.lazy.rememberScalingLazyListState
import androidx.wear.compose.material.CircularProgressIndicator
import androidx.wear.compose.material.Icon
import androidx.wear.compose.material.Switch
import androidx.wear.compose.material.Text
import androidx.wear.compose.material.ToggleChip
import androidx.wear.compose.material.ToggleChipDefaults
import com.remoteci.watch.data.ClassStateSnapshot
import com.remoteci.watch.data.ConnectionManager
import com.remoteci.watch.data.CourseEntry
import com.remoteci.watch.data.ExtensionDefinition
import com.remoteci.watch.data.GitHubAsset
import com.remoteci.watch.data.GitHubRelease
import com.remoteci.watch.data.LanPluginCandidate
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.ScheduleBundle
import com.remoteci.watch.data.ScheduleDay
import com.remoteci.watch.data.SubjectEntry
import com.remoteci.watch.data.UserProfile
import com.remoteci.watch.data.UpdateManager
import com.remoteci.watch.data.UpdateChannel
import com.remoteci.watch.data.WatchSettings
import java.time.Duration
import java.time.LocalDate
import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneOffset
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

internal enum class SwapMode { Exchange, Replace }
internal enum class LessonTarget { Source, Target }
internal data class WatchSurfaceLayout(
    val width: Dp,
    val height: Dp,
    val scale: Dp,
    val clipToCircle: Boolean,
)

internal data class HomeTimeChipWidthBounds(
    val minWidth: Dp,
    val maxWidth: Dp,
)

/** 时间按钮由文本固有宽度决定实际宽度，同时保留圆屏内的安全上下限。 */
internal fun homeTimeChipWidthBounds(diameter: Dp): HomeTimeChipWidthBounds =
    HomeTimeChipWidthBounds(
        minWidth = diameter * .39f,
        maxWidth = diameter * .72f,
    )

/** 圆屏保持圆形安全画布，矩形屏使用完整可用区域。 */
internal fun calculateWatchSurfaceLayout(
    maxWidth: Dp,
    maxHeight: Dp,
    isScreenRound: Boolean,
): WatchSurfaceLayout {
    val diameter = minOf(maxWidth, maxHeight)
    return if (isScreenRound) {
        WatchSurfaceLayout(diameter, diameter, diameter, clipToCircle = true)
    } else {
        WatchSurfaceLayout(maxWidth, maxHeight, diameter, clipToCircle = false)
    }
}

internal data class LessonChoice(
    val id: String,
    val index: Int,
    val periodLabel: String,
    val subject: String,
    val time: String,
    val commandValue: String,
    val enabled: Boolean,
)

/** 当前 M3 配色的快捷入口，所有屏幕统一从主题取色。 */
private val palette: WatchPalette
    @Composable get() = LocalWatchPalette.current

@Composable
internal fun LoginScreen(
    settings: WatchSettings,
    password: String,
    state: ConnectionManager.State,
    onSettingsChange: (WatchSettings) -> Unit,
    onPasswordChange: (String) -> Unit,
    lanPlugins: List<LanPluginCandidate>,
    lanDiscoveryStatus: String?,
    lanDiscoveryScanning: Boolean,
    onScanLanPlugins: () -> Unit,
    onSelectLanPlugin: (LanPluginCandidate) -> Unit,
    onLogin: () -> Unit,
) = WatchList(title = "登录 RemoteCI") {
    item { Input(settings.username, { onSettingsChange(settings.copy(username = it)) }, "ID") }
    item { Input(password, onPasswordChange, "密码", password = true) }
    item { Input(settings.cloudServerUrl, { onSettingsChange(settings.copy(cloudServerUrl = it)) }, "云端地址") }
    if (settings.cloudServerUrl.trim().startsWith("http://")) {
        item { Hint("云端地址使用明文 HTTP，账号密码可能被同一局域网窃听，建议改用 HTTPS") }
    }
    item {
        ActionButton(
            if (lanDiscoveryScanning) "正在扫描…" else "扫描局域网设备",
            Icons.Rounded.Wifi,
            !lanDiscoveryScanning,
            onScanLanPlugins,
        )
    }
    if (!lanDiscoveryStatus.isNullOrBlank()) item { Hint(lanDiscoveryStatus) }
    lanPlugins.forEach { plugin ->
        item {
            ActionButton(
                "${plugin.instanceName} · ${plugin.host}:${plugin.port}",
                Icons.Rounded.Wifi,
                !lanDiscoveryScanning,
                onClick = { onSelectLanPlugin(plugin) },
            )
        }
    }
    item { ActionButton("安全登录", Icons.Rounded.Wifi, settings.username.isNotBlank() && password.length >= 8, onLogin) }
    item { Hint(describeConnectionForScreen(state)) }
}

@Composable
internal fun HomeScreen(
    connectionState: ConnectionManager.State,
    snapshot: ClassStateSnapshot?,
    user: UserProfile?,
    onOpenScheduleOverview: () -> Unit,
    onOpenScheduleChange: () -> Unit,
    onQuickSwapCourse: (() -> Unit)?,
    onOpenNotification: () -> Unit,
    onOpenSettings: () -> Unit,
    onRetryConnection: () -> Unit,
) {
    var now by remember(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes) {
        mutableStateOf(pluginLocalNow(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes, 0L, 0L))
    }
    // 以插件快照的 UTC 生成时间为基准推算“插件本地当前时间”，避免两端时区/时钟不一致时进度环为空。
    LaunchedEffect(snapshot?.generatedAt, snapshot?.timeZoneOffsetMinutes) {
        val baseElapsed = SystemClock.elapsedRealtime()
        while (true) {
            now = pluginLocalNow(
                snapshot?.generatedAt,
                snapshot?.timeZoneOffsetMinutes,
                baseElapsed,
                SystemClock.elapsedRealtime(),
            )
            delay(30_000)
        }
    }
    WatchSurface { diameter ->
        val pagerState = rememberPagerState(pageCount = { 2 })
        Box(Modifier.fillMaxSize()) {
            // Wear Compose 的 VerticalPager 默认内置表冠/旋转表圈支持：
            // 一格贴合一页、自动管理旋转焦点，并按设备分辨率（表冠/表圈）自适应灵敏度。
            VerticalPager(
                state = pagerState,
                modifier = Modifier.fillMaxSize(),
            ) { page ->
                if (page == 0) {
                    HomeStatusPage(
                        connectionState = connectionState,
                        snapshot = snapshot,
                        user = user,
                        now = now,
                        diameter = diameter,
                        onQuickSwapCourse = onQuickSwapCourse,
                        onRetryConnection = onRetryConnection,
                    )
                } else {
                    HomeMenuPage(
                        user = user,
                        onOpenScheduleOverview = onOpenScheduleOverview,
                        onOpenScheduleChange = onOpenScheduleChange,
                        onOpenNotification = onOpenNotification,
                        onOpenSettings = onOpenSettings,
                    )
                }
            }
            HomePageIndicator(
                currentPage = pagerState.currentPage,
                diameter = diameter,
                modifier = Modifier.align(Alignment.CenterEnd),
            )
        }
    }
}

@Composable
private fun HomeStatusPage(
    connectionState: ConnectionManager.State,
    snapshot: ClassStateSnapshot?,
    user: UserProfile?,
    now: LocalTime,
    diameter: Dp,
    onQuickSwapCourse: (() -> Unit)?,
    onRetryConnection: () -> Unit,
) {
    // 下课和即将上课时，主页的主课程入口代表即将发生的下一节课；
    // 文案、时间和点击后的换课源课必须始终来自同一个目标，避免“显示数学却换了语文”。
    val courseContent = homeCourseContent(snapshot)
    val timeChipWidth = homeTimeChipWidthBounds(diameter)
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(palette.homePanel)
            .clickable(
                enabled = connectionState is ConnectionManager.State.Error,
                onClick = onRetryConnection,
            ),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(diameter * .26f))
        Row(
            modifier = Modifier.height(diameter * .13f),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.Center,
        ) {
            if (shouldShowStateProgress(snapshot)) {
                CircularProgressIndicator(
                    progress = lessonProgress(snapshot?.currentTimeLayoutItem, now),
                    modifier = Modifier.size(diameter * .125f),
                    strokeWidth = diameter * .02f,
                    indicatorColor = palette.progressActive,
                    trackColor = palette.progressTrack,
                )
                Spacer(Modifier.width(diameter * .04f))
            }
            Text(
                describeClassState(snapshot),
                color = Color.Black,
                fontSize = 22.sp,
                fontWeight = FontWeight.Bold,
            )
        }
        Spacer(Modifier.height(diameter * .10f))
        HomeInfoChip(
            text = courseContent.subject,
            icon = Icons.Rounded.SwapHoriz,
            filled = true,
            modifier = Modifier.width(diameter * .39f).height(diameter * .14f),
            fontSize = if (courseContent.subject.length <= 2) 16.sp else 12.sp,
            iconSize = diameter * .055f,
            onClick = onQuickSwapCourse,
        )
        Spacer(Modifier.height(diameter * .035f))
        HomeInfoChip(
            text = extractTimeRange(courseContent.timeLayoutItem).ifBlank { "--:--" },
            filled = false,
            // widthIn 不强制固定宽度：Row 会按时间文字的固有宽度扩展，并受圆屏安全上限约束。
            modifier = Modifier
                .widthIn(min = timeChipWidth.minWidth, max = timeChipWidth.maxWidth)
                .height(diameter * .105f),
            fontSize = 12.sp,
        )
        if (canViewExtendedSchedule(user) && shouldShowNextLessonSummary(snapshot?.currentState)) {
            Spacer(Modifier.height(diameter * .035f))
            Text(
                "下一节课是：${snapshot?.nextClassSubject ?: "无"}",
                color = Color.Black,
                fontSize = 14.sp,
                fontWeight = FontWeight.Bold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

@Composable
private fun HomeMenuPage(
    user: UserProfile?,
    onOpenScheduleOverview: () -> Unit,
    onOpenScheduleChange: () -> Unit,
    onOpenNotification: () -> Unit,
    onOpenSettings: () -> Unit,
) {
    val actions = homeActionLabels(user).map { label ->
        when (label) {
            "课表" -> HomeAction(label, Icons.AutoMirrored.Rounded.List, onOpenScheduleOverview)
            "换课" -> HomeAction(label, Icons.Rounded.SwapHoriz, onOpenScheduleChange)
            "控制" -> HomeAction(label, Icons.Rounded.Wifi, onOpenNotification)
            else -> HomeAction(label, Icons.Rounded.Settings, onOpenSettings)
        }
    }
    // 首页菜单不显示“菜单”标题，让选项直接占满一屏，避免旋转翻页后还要滚动。
    WatchList(title = "菜单", showTitle = false) {
        actions.forEach { action ->
            item { ActionButton(action.label, action.icon, true, action.onClick) }
        }
    }
}

@Composable
private fun HomePageIndicator(currentPage: Int, diameter: Dp, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.padding(end = diameter * .035f),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        repeat(2) { page ->
            Box(
                Modifier
                    .size(9.dp)
                    .clip(CircleShape)
                    .background(if (currentPage == page) palette.primaryContainer else Color.White.copy(alpha = .92f)),
            )
        }
    }
}

@Composable
internal fun ScheduleOverviewScreen(
    day: ScheduleDay?,
    connectionReady: Boolean,
    onRequestSchedule: () -> Unit,
    onPickDate: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "课表") {
    if (day == null) {
        item { Hint("尚未同步课表") }
        if (shouldOfferSchedulePull(day, connectionReady))
            item { ActionButton("向插件拉取", Icons.Rounded.Refresh, true, onRequestSchedule) }
    } else {
        // 日期标题同时承担切换入口，单页始终只渲染一天，避免七日内容在圆屏上过长。
        item { ActionButton(scheduleDateTitle(day.date), null, true, onPickDate) }
        if (day.courses.isEmpty()) item { Hint("当天没有课程") }
        day.courses.forEach { course ->
            item { LessonSummary(course) }
        }
    }
    item { BackButton(onBack) }
}

internal fun shouldOfferSchedulePull(day: ScheduleDay?, connectionReady: Boolean): Boolean =
    day == null && connectionReady

@Composable
internal fun ScheduleDatePickerScreen(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    onSelect: (ScheduleDay) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "选择日期") {
    val days = availableScheduleDays(bundle, afterSchool)
    if (days.isEmpty()) item { Hint("没有可查看的课表") }
    days.forEach { day ->
        item { ActionButton(scheduleDateTitle(day.date), null, true, onClick = { onSelect(day) }) }
    }
    item { BackButton(onBack) }
}

@Composable
internal fun DayPickerScreen(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    onSelect: (ScheduleDay) -> Unit,
    onBack: () -> Unit,
) =
    WatchList(title = "选择日期") {
        val days = availableScheduleDays(bundle, afterSchool)
        if (days.isEmpty()) item { Hint("没有可换课的日期") }
        days.forEach { day ->
            item { ActionButton(scheduleDateTitle(day.date), null, day.enabled, onClick = { onSelect(day) }) }
        }
        item { BackButton(onBack) }
    }

@Composable
internal fun SwapScreen(
    day: ScheduleDay?,
    mode: SwapMode,
    sourceLesson: LessonChoice?,
    targetLesson: LessonChoice?,
    replacementSubject: String?,
    connectionReady: Boolean,
    resultText: String?,
    onModeChange: (SwapMode) -> Unit,
    onPickSource: () -> Unit,
    onPickTarget: () -> Unit,
    onSubmit: () -> Unit,
) = WatchList(title = day?.date ?: "换课") {
    item { ModeSelector(mode, onModeChange) }
    item { LessonButton("原课", sourceLesson?.subject ?: "选择", onPickSource) }
    item { LessonButton("目标", if (mode == SwapMode.Exchange) targetLesson?.subject ?: "选择" else replacementSubject ?: "选择科目", onPickTarget) }
    val validTarget = if (mode == SwapMode.Exchange) targetLesson != null && targetLesson.index != sourceLesson?.index else replacementSubject != null
    item { ActionButton(if (mode == SwapMode.Exchange) "确认交换" else "确认替换", Icons.Rounded.Check, connectionReady && sourceLesson != null && validTarget, onSubmit) }
    if (!resultText.isNullOrBlank()) item { Hint(resultText) }
}

@Composable
internal fun LessonPickerScreen(
    title: String,
    lessons: List<LessonChoice>,
    selectedIndex: Int?,
    excludedIndex: Int?,
    onSelect: (LessonChoice) -> Unit,
) = WatchList(title = title) {
    lessons.forEach { lesson -> item {
        ActionButton(
            "${if (lesson.index == selectedIndex) "✓ " else ""}${lesson.periodLabel} ${lesson.subject}",
            null,
            lesson.enabled && lesson.index != excludedIndex,
            onClick = { onSelect(lesson) },
        )
    } }
}

@Composable
internal fun SubjectPickerScreen(
    subjects: List<SubjectEntry>,
    selectedSubjectId: String?,
    onSelect: (SubjectEntry) -> Unit,
) = WatchList(title = "选择科目") {
    subjects.forEach { subject -> item {
        ActionButton("${if (subject.id == selectedSubjectId) "✓ " else ""}${subject.name}", null, true, onClick = { onSelect(subject) })
    } }
}

@Composable
internal fun ControlScreen(
    snapshot: ClassStateSnapshot?,
    user: UserProfile?,
    extensions: List<ExtensionDefinition>,
    resultText: String?,
    onTeacherComing: () -> Unit,
    onOpenNotification: () -> Unit,
    onClearNotifications: () -> Unit,
    onToggleMainMenu: () -> Unit,
    onOpenVolume: () -> Unit,
    onOpenPower: () -> Unit,
    onRunExtension: (ExtensionDefinition) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "控制") {
    val canNotify = user?.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) == true
    val canControlSystem = user?.has(Protocol.PERMISSION_SYSTEM_CONTROL) == true
    item { ActionButton("老师来了", Icons.Rounded.School, canNotify, onTeacherComing) }
    item { ActionButton("发送通知", Icons.Rounded.EditNotifications, canNotify, onOpenNotification) }
    if (shouldShowClearNotifications(snapshot)) item {
        ActionButton("清除通知", Icons.Rounded.NotificationsOff, canNotify, onClearNotifications)
    }
    item {
        ActionButton(
            mainMenuActionLabel(snapshot),
            if (snapshot?.isMainMenuVisible == false) Icons.Rounded.Visibility else Icons.Rounded.VisibilityOff,
            canControlSystem,
            onToggleMainMenu,
        )
    }
    item {
        ActionButton(
            "音量",
            if (snapshot?.isMuted == true) Icons.AutoMirrored.Rounded.VolumeOff else Icons.AutoMirrored.Rounded.VolumeUp,
            canControlSystem && snapshot?.isVolumeControlAvailable == true,
            onOpenVolume,
        )
    }
    item { ActionButton("电源", Icons.Rounded.PowerSettingsNew, canControlSystem, onOpenPower) }
    // 其他插件通过 RemoteCI 注册的扩展功能：按当前用户权限过滤后动态显示在控制菜单底部。
    visibleExtensionsFor(user, extensions).forEach { extension ->
        item {
            ActionButton(
                extension.displayName,
                extensionIcon(extension.icon),
                enabled = true,
                onClick = { onRunExtension(extension) },
            )
        }
    }
    if (!resultText.isNullOrBlank()) item { Hint(resultText) }
    item { BackButton(onBack) }
}

internal fun shouldShowClearNotifications(snapshot: ClassStateSnapshot?): Boolean =
    snapshot?.isNotificationPlaying == true

internal fun mainMenuActionLabel(snapshot: ClassStateSnapshot?): String =
    if (snapshot?.isMainMenuVisible == false) "显示主菜单" else "隐藏主菜单"

/** 扩展功能按当前用户有效权限过滤；隐藏按钮不构成安全控制，插件执行端会再次校验。 */
internal fun visibleExtensionsFor(
    user: UserProfile?,
    extensions: List<ExtensionDefinition>,
): List<ExtensionDefinition> = extensions.filter { user?.has(it.requiredPermission) == true }

/** Material 图标名白名单映射；未知或缺失时返回 null，界面回退为纯文字。 */
internal fun extensionIcon(icon: String?): ImageVector? = when (icon?.trim()?.lowercase()) {
    "school" -> Icons.Rounded.School
    "notification", "notifications", "message" -> Icons.Rounded.EditNotifications
    "volume", "volumeup" -> Icons.AutoMirrored.Rounded.VolumeUp
    "power", "poweroff" -> Icons.Rounded.PowerSettingsNew
    "settings", "gear" -> Icons.Rounded.Settings
    "update", "systemupdate" -> Icons.Rounded.SystemUpdate
    "download" -> Icons.Rounded.Download
    "restart", "reboot" -> Icons.Rounded.RestartAlt
    "swap", "exchange" -> Icons.Rounded.SwapHoriz
    "wifi", "connect" -> Icons.Rounded.Wifi
    "visibility", "show" -> Icons.Rounded.Visibility
    "hide", "hidden" -> Icons.Rounded.VisibilityOff
    "clear", "clearnotifications" -> Icons.Rounded.NotificationsOff
    else -> null
}

/** 按参数 schema 生成初始表单值；switch 默认 false，其余使用注册的默认值。 */
internal fun defaultExtensionArgs(extension: ExtensionDefinition): Map<String, String?> =
    extension.parameters.associate { param ->
        param.key to when (param.type) {
            Protocol.EXT_PARAM_SWITCH -> (param.defaultValue ?: "false").let { if (it == "true") "true" else "false" }
            else -> param.defaultValue
        }
    }

/** select 参数点击后的下一个候选项（循环切换；当前值不在候选中时从第一项开始）。 */
internal fun nextSelectValue(options: List<String>, current: String?): String {
    if (options.isEmpty()) return current ?: ""
    return options[(options.indexOf(current) + 1).mod(options.size)]
}

/** 扩展参数输入页：按注册的 schema 渲染字段，提交后由调用方发送 RunExtension 命令。 */
@Composable
internal fun ExtensionFormScreen(
    extension: ExtensionDefinition,
    connectionReady: Boolean,
    resultText: String?,
    onSubmit: (Map<String, String?>) -> Unit,
    onBack: () -> Unit,
) {
    var values by remember(extension.id) { mutableStateOf(defaultExtensionArgs(extension)) }
    WatchList(title = extension.displayName) {
        extension.parameters.forEach { param -> item {
            when (param.type) {
                Protocol.EXT_PARAM_SWITCH -> Toggle(param.label, values[param.key] == "true") {
                    values = values + (param.key to it.toString())
                }

                Protocol.EXT_PARAM_SELECT -> ActionButton(
                    "${param.label}：${values[param.key] ?: ""}",
                    null,
                    param.options.isNotEmpty(),
                    onClick = { values = values + (param.key to nextSelectValue(param.options, values[param.key])) },
                )

                else -> Input(
                    values[param.key] ?: "",
                    { values = values + (param.key to it) },
                    param.label,
                )
            }
        } }
        item { ActionButton("执行", Icons.Rounded.Check, connectionReady, onClick = { onSubmit(values) }) }
        if (!resultText.isNullOrBlank()) item { Hint(resultText) }
        item { BackButton(onBack) }
    }
}

@Composable
internal fun VolumeScreen(
    volumePercent: Int,
    muted: Boolean,
    available: Boolean,
    onVolumeChange: (Int) -> Unit,
    onMutedChange: (Boolean) -> Unit,
    onBack: () -> Unit,
) {
    var localVolume by remember(volumePercent) { mutableStateOf(volumePercent.coerceIn(0, 100)) }
    val focusRequester = remember { FocusRequester() }
    LaunchedEffect(Unit) { focusRequester.requestFocus() }

    WatchList(title = "音量") {
        item { Hint(if (muted) "当前已静音 · 音量 $localVolume%" else "当前音量 $localVolume%") }
        item {
            Slider(
                value = localVolume.toFloat(),
                onValueChange = { localVolume = it.toInt().coerceIn(0, 100) },
                onValueChangeFinished = { onVolumeChange(localVolume) },
                valueRange = 0f..100f,
                enabled = available,
                modifier = Modifier.fillMaxWidth(.78f)
                    .focusRequester(focusRequester)
                    .onRotaryScrollEvent { event ->
                        val adjusted = adjustVolumeForRotary(localVolume, event.verticalScrollPixels)
                        if (adjusted != localVolume) {
                            localVolume = adjusted
                            onVolumeChange(adjusted)
                        }
                        true
                    }
                    .focusable(),
            )
        }
        item { Hint("可触摸拖动，也可旋转表冠调节") }
        item {
            ActionButton(
                if (muted) "取消静音" else "静音",
                if (muted) Icons.AutoMirrored.Rounded.VolumeUp else Icons.AutoMirrored.Rounded.VolumeOff,
                available,
                onClick = { onMutedChange(!muted) },
            )
        }
        item { BackButton(onBack) }
    }
}

internal fun adjustVolumeForRotary(current: Int, verticalScrollPixels: Float): Int {
    val step = when {
        verticalScrollPixels > 0 -> 2
        verticalScrollPixels < 0 -> -2
        else -> 0
    }
    return (current + step).coerceIn(0, 100)
}

@Composable
internal fun PowerScreen(
    sleepAvailable: Boolean,
    hibernateAvailable: Boolean,
    onPowerAction: (Int) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "电源") {
    item { ActionButton("关机", Icons.Rounded.PowerSettingsNew, true, onClick = { onPowerAction(Protocol.POWER_SHUTDOWN) }) }
    item { ActionButton("重启", Icons.Rounded.RestartAlt, true, onClick = { onPowerAction(Protocol.POWER_RESTART) }) }
    item { ActionButton("睡眠", Icons.Rounded.PowerSettingsNew, sleepAvailable, onClick = { onPowerAction(Protocol.POWER_SLEEP) }) }
    if (hibernateAvailable) item {
        ActionButton("休眠", Icons.Rounded.PowerSettingsNew, true, onClick = { onPowerAction(Protocol.POWER_HIBERNATE) })
    }
    item { BackButton(onBack) }
}

@Composable
internal fun NotificationScreen(
    title: String,
    message: String,
    forceSenderInTitle: Boolean,
    effectEnabled: Boolean,
    soundEnabled: Boolean,
    speechEnabled: Boolean,
    resultText: String?,
    onTitleChange: (String) -> Unit,
    onMessageChange: (String) -> Unit,
    onEffectEnabledChange: (Boolean) -> Unit,
    onSoundEnabledChange: (Boolean) -> Unit,
    onSpeechEnabledChange: (Boolean) -> Unit,
    onSend: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "发送消息") {
    item { Input(title, onTitleChange, "标题") }
    if (forceSenderInTitle) item { Hint("将显示发送人") }
    item { Input(message, onMessageChange, "正文") }
    item { Toggle("启用提醒强调特效", effectEnabled, onEffectEnabledChange) }
    item { Toggle("启用提醒音效", soundEnabled, onSoundEnabledChange) }
    item { Toggle("启用提醒语言", speechEnabled, onSpeechEnabledChange) }
    // 标题与正文均可留空，插件会为留空项兜底为默认标题/原标题。
    item { ActionButton("发送并等待回执", Icons.Rounded.EditNotifications, true, onSend) }
    if (!resultText.isNullOrBlank()) item { Hint(resultText) }
    item { BackButton(onBack) }
}

@Composable
internal fun SettingsScreen(
    onOpenConnection: () -> Unit,
    onOpenNotifications: () -> Unit,
    onOpenAppearance: () -> Unit,
    onOpenUpdate: () -> Unit,
    onOpenDeveloper: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "设置") {
    item { ActionButton("连接", Icons.Rounded.Wifi, true, onOpenConnection) }
    item { ActionButton("通知", Icons.Rounded.EditNotifications, true, onOpenNotifications) }
    item { ActionButton("外观", Icons.Rounded.Palette, true, onOpenAppearance) }
    item { ActionButton("更新", Icons.Rounded.SystemUpdate, true, onOpenUpdate) }
    item { ActionButton("开发者", Icons.Rounded.Code, true, onOpenDeveloper) }
    item { BackButton(onBack) }
}

@Composable
internal fun AppearanceSettingsScreen(
    currentThemeId: String,
    onThemeChange: (String) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "外观") {
    item { Hint("选择配色方案") }
    WatchPalette.All.forEach { option ->
        item {
            ThemeOptionButton(
                option = option,
                selected = option.id == currentThemeId,
                onClick = { onThemeChange(option.id) },
            )
        }
    }
    item { BackButton(onBack) }
}

/** 外观设置页的配色选项：色点 + 名称 + 当前选中标记。 */
@Composable
private fun ThemeOptionButton(
    option: WatchPalette,
    selected: Boolean,
    onClick: () -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(.78f).height(38.dp).clip(RoundedCornerShape(19.dp))
            .background(if (selected) palette.buttonContainer else Color.Transparent)
            .then(if (selected) Modifier else Modifier.border(.5.dp, Color.White.copy(.35f), RoundedCornerShape(19.dp)))
            .clickable(onClick = onClick),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(14.dp).clip(CircleShape).background(option.progressActive))
        Spacer(Modifier.width(8.dp))
        Text(
            option.label,
            color = if (selected) palette.onButtonContainer else Color.White,
            fontSize = 12.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        if (selected) {
            Spacer(Modifier.width(6.dp))
            Icon(Icons.Rounded.Check, null, tint = palette.onButtonContainer, modifier = Modifier.size(15.dp))
        }
    }
}

/** 更新页状态。 */
private sealed interface UpdateUiState {
    data object Idle : UpdateUiState
    data object Checking : UpdateUiState
    data class UpToDate(val message: String) : UpdateUiState
    data object Downloading : UpdateUiState
    data object Installing : UpdateUiState
    data class Available(
        val latestVersion: String,
        val release: GitHubRelease,
        val asset: GitHubAsset,
    ) : UpdateUiState

    data class Error(val message: String) : UpdateUiState
}

@Composable
internal fun UpdateScreen(
    context: Context,
    currentVersion: String,
    serverVersion: String?,
    updateChannel: UpdateChannel,
    forceUpdate: Boolean,
    onUpdateOptionsChange: (UpdateChannel, Boolean) -> Unit,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var state by remember { mutableStateOf<UpdateUiState>(UpdateUiState.Idle) }

    fun checkUpdate(): Unit {
        val allowedVersion = serverVersion
        if (allowedVersion.isNullOrBlank()) {
            state = UpdateUiState.Error("请先连接 WebUI，再检查手表更新")
            return
        }
        state = UpdateUiState.Checking
        scope.launch {
            state = try {
                val selected = UpdateManager.selectCompatibleUpdate(
                    releases = UpdateManager.fetchReleases(),
                    currentVersion = currentVersion,
                    serverVersion = allowedVersion,
                    channel = updateChannel,
                    force = forceUpdate,
                )
                if (selected != null) {
                    UpdateUiState.Available(
                        UpdateManager.versionFromTag(selected.release.tagName),
                        selected.release,
                        selected.asset,
                    )
                } else {
                    UpdateUiState.UpToDate("已是 WebUI v$allowedVersion 允许的最新版本")
                }
            } catch (error: Exception) {
                UpdateUiState.Error(error.message ?: "检查更新失败")
            }
        }
    }

    WatchList(title = "更新") {
        item { Hint("当前版本 v$currentVersion") }
        item { Hint("已连接 WebUI v${serverVersion ?: "未知"}") }
        item { Hint("更新渠道") }
        item {
            UpdateChannelSelector(updateChannel) { channel ->
                state = UpdateUiState.Idle
                onUpdateOptionsChange(channel, forceUpdate)
            }
        }
        item {
            Toggle("强制更新", forceUpdate) { enabled ->
                state = UpdateUiState.Idle
                onUpdateOptionsChange(updateChannel, enabled)
            }
        }
        if (forceUpdate) item { Hint("允许同版本重新下载并覆盖安装，不允许降级") }
        when (val current = state) {
            UpdateUiState.Idle, UpdateUiState.Checking -> {
                if (current == UpdateUiState.Checking) {
                    item { CircularProgressIndicator(modifier = Modifier.size(34.dp)) }
                }
                item {
                    ActionButton(
                        "检查更新",
                        Icons.Rounded.SystemUpdate,
                        enabled = current != UpdateUiState.Checking,
                        onClick = ::checkUpdate,
                    )
                }
            }

            is UpdateUiState.UpToDate -> {
                item { Hint(current.message) }
                item {
                    ActionButton("重新检查", Icons.Rounded.SystemUpdate, true, onClick = ::checkUpdate)
                }
            }

            is UpdateUiState.Available -> {
                val reinstalling = UpdateManager.compareVersions(current.latestVersion, currentVersion) == 0
                item { Hint(if (reinstalling) "可强制覆盖安装 v${current.latestVersion}" else "发现新版本 v${current.latestVersion}") }
                item { Hint(releaseNotesPreview(current.release.body)) }
                item {
                    ActionButton(if (reinstalling) "重新下载并覆盖" else "下载并安装", Icons.Rounded.Download, true, onClick = {
                        state = UpdateUiState.Downloading
                        scope.launch {
                            state = try {
                                val allowedVersion = serverVersion
                                    ?: throw IllegalStateException("WebUI 连接已断开，请重新检查更新")
                                if (UpdateManager.compareVersions(current.latestVersion, allowedVersion) > 0) {
                                    throw IllegalStateException("手表版本不得超过已连接 WebUI v$allowedVersion")
                                }
                                val apk = UpdateManager.downloadApk(context, current.asset)
                                // 下载期间连接可能断开或切换到更低版本的 WebUI，安装前必须读取实时上限。
                                val latestAllowedVersion = ConnectionManager.serverVersion.value
                                    ?: throw IllegalStateException("WebUI 连接已断开，请重新检查更新")
                                if (UpdateManager.compareVersions(current.latestVersion, latestAllowedVersion) > 0) {
                                    throw IllegalStateException("手表版本不得超过已连接 WebUI v$latestAllowedVersion")
                                }
                                UpdateManager.installApk(context, apk)
                                UpdateUiState.Installing
                            } catch (error: Exception) {
                                UpdateUiState.Error(error.message ?: "更新失败")
                            }
                        }
                    })
                }
            }

            UpdateUiState.Downloading -> {
                item { Hint("正在下载更新包…") }
                item { CircularProgressIndicator(modifier = Modifier.size(34.dp)) }
            }

            UpdateUiState.Installing -> {
                item { Hint("正在安装，请按系统提示确认…") }
                item { CircularProgressIndicator(modifier = Modifier.size(34.dp)) }
            }

            is UpdateUiState.Error -> {
                item { Hint(current.message) }
                item {
                    ActionButton("重新检查", Icons.Rounded.SystemUpdate, true, onClick = ::checkUpdate)
                }
            }
        }
        item { BackButton(onBack) }
    }
}

@Composable
private fun UpdateChannelSelector(channel: UpdateChannel, onChange: (UpdateChannel) -> Unit) {
    Row(Modifier.width(150.dp).height(30.dp).clip(RoundedCornerShape(15.dp)).background(palette.disabledContainer)) {
        listOf(UpdateChannel.STABLE to "正式版", UpdateChannel.BETA to "Beta版").forEach { (value, label) ->
            Box(
                Modifier.weight(1f).fillMaxSize()
                    .background(if (channel == value) palette.buttonContainer else Color.Transparent)
                    .clickable { onChange(value) },
                contentAlignment = Alignment.Center,
            ) {
                Text(label, color = if (channel == value) palette.onButtonContainer else Color.White, fontSize = 11.sp)
            }
        }
    }
}

/** 截取 release 说明供手表小屏展示。 */
private fun releaseNotesPreview(body: String?, maxLength: Int = 180): String {
    if (body.isNullOrBlank()) return "暂无更新说明"
    val plain = body.replace("\r", "").trim()
    return if (plain.length <= maxLength) plain else plain.take(maxLength) + "…"
}

@Composable
internal fun ConnectionSettingsScreen(
    settings: WatchSettings,
    stateText: String,
    onSettingsChange: (WatchSettings) -> Unit,
    onReconnect: () -> Unit,
    onLogout: () -> Unit,
    onBack: () -> Unit,
) {
    var portText by remember(settings.lanPort) { mutableStateOf(settings.lanPort.toString()) }
    WatchList(title = "连接") {
        item { Hint(stateText) }
        item { Input(settings.cloudServerUrl, { onSettingsChange(settings.copy(cloudServerUrl = it)) }, "云端地址") }
        item { Input(settings.lanHost, { onSettingsChange(settings.copy(lanHost = it)) }, "电脑 IP") }
        item {
            Input(portText, { value ->
                portText = value
                parseLanPort(value)?.let { onSettingsChange(settings.copy(lanPort = it)) }
            }, "局域网端口")
        }
        item { ActionButton("保存并重连", Icons.Rounded.Wifi, true, onReconnect) }
        item { ActionButton("退出账号", null, true, onLogout, subtle = true) }
        item { BackButton(onBack) }
    }
}

@Composable
internal fun DeveloperSettingsScreen(
    cloudConnectionEnabled: Boolean,
    lanConnectionEnabled: Boolean,
    onCloudConnectionEnabledChange: (Boolean) -> Unit,
    onLanConnectionEnabledChange: (Boolean) -> Unit,
    onReconnect: () -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "开发者") {
    item { Hint("关闭云端后只能依赖局域网直连；关闭局域网后只使用云端。密码登录仍会临时使用云端完成认证。") }
    if (shouldShowCloudConnectionToggle(developerSettings = true)) {
        item { Toggle("云服务器", cloudConnectionEnabled, onCloudConnectionEnabledChange) }
    }
    if (shouldShowLanConnectionToggle(developerSettings = true)) {
        item { Toggle("局域网直连", lanConnectionEnabled, onLanConnectionEnabledChange) }
    }
    item { ActionButton("应用并重连", Icons.Rounded.Wifi, true, onReconnect) }
    item { BackButton(onBack) }
}

internal fun shouldShowCloudConnectionToggle(developerSettings: Boolean): Boolean = developerSettings
internal fun shouldShowLanConnectionToggle(developerSettings: Boolean): Boolean = developerSettings
internal fun parseLanPort(value: String): Int? = value.trim().toIntOrNull()?.takeIf { it in 1..65535 }

@Composable
internal fun NotificationSettingsScreen(
    settings: WatchSettings,
    onSettingsChange: (WatchSettings) -> Unit,
    onBack: () -> Unit,
) = WatchList(title = "通知") {
    item { Toggle("上课", settings.receiveOnClass) { onSettingsChange(settings.copy(receiveOnClass = it)) } }
    item { Toggle("下课", settings.receiveOnBreaking) { onSettingsChange(settings.copy(receiveOnBreaking = it)) } }
    item { Toggle("放学", settings.receiveAfterSchool) { onSettingsChange(settings.copy(receiveAfterSchool = it)) } }
    item { Toggle("课表变更", settings.receiveScheduleChanged) { onSettingsChange(settings.copy(receiveScheduleChanged = it)) } }
    item { Toggle("自定义消息", settings.receiveCustom) { onSettingsChange(settings.copy(receiveCustom = it)) } }
    item { Toggle("ClassIsland 自动化", settings.receiveAutomationNotifications) { onSettingsChange(settings.copy(receiveAutomationNotifications = it)) } }
    item { Toggle("其他插件通知", settings.receivePluginNotifications) { onSettingsChange(settings.copy(receivePluginNotifications = it)) } }
    item { BackButton(onBack) }
}

@Composable
private fun WatchList(
    title: String,
    showTitle: Boolean = true,
    content: androidx.wear.compose.foundation.lazy.ScalingLazyListScope.() -> Unit,
) {
    WatchSurface {
        ScalingLazyColumn(
            modifier = Modifier.fillMaxSize(),
            state = rememberScalingLazyListState(),
            horizontalAlignment = Alignment.CenterHorizontally,
            contentPadding = PaddingValues(top = 34.dp, bottom = 58.dp),
        ) {
            if (showTitle) {
                item { Text(title, color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold) }
                item { Spacer(Modifier.height(6.dp)) }
            }
            content()
        }
    }
}

@Composable
private fun WatchSurface(content: @Composable BoxScope.(Dp) -> Unit) {
    val isScreenRound = LocalConfiguration.current.isScreenRound
    BoxWithConstraints(Modifier.fillMaxSize().background(Color.Black), contentAlignment = Alignment.Center) {
        val layout = calculateWatchSurfaceLayout(maxWidth, maxHeight, isScreenRound)
        val surfaceModifier = Modifier.size(layout.width, layout.height)
            .then(if (layout.clipToCircle) Modifier.clip(CircleShape) else Modifier)
            .background(Color.Black)
        Box(surfaceModifier, contentAlignment = Alignment.Center) { content(layout.scale) }
    }
}

@Composable
private fun ActionButton(
    label: String,
    icon: ImageVector?,
    enabled: Boolean,
    onClick: () -> Unit,
    subtle: Boolean = false,
) {
    Row(
        modifier = Modifier.fillMaxWidth(.78f).height(38.dp).clip(RoundedCornerShape(19.dp))
            .background(if (!enabled) palette.disabledContainer else if (subtle) Color.Transparent else palette.buttonContainer)
            .then(if (subtle) Modifier.border(.5.dp, Color.White.copy(.35f), RoundedCornerShape(19.dp)) else Modifier)
            .clickable(enabled = enabled, onClick = onClick),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) { Icon(icon, null, tint = if (subtle) Color.White else palette.onButtonContainer, modifier = Modifier.size(17.dp)); Spacer(Modifier.width(5.dp)) }
        Text(label, color = if (!enabled) palette.disabledContent else if (subtle) Color.White else palette.onButtonContainer, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable private fun BackButton(onClick: () -> Unit) = ActionButton("返回", Icons.AutoMirrored.Rounded.ArrowBack, true, onClick, subtle = true)

private data class HomeAction(val label: String, val icon: ImageVector, val onClick: () -> Unit)

internal fun homeActionLabels(user: UserProfile?): List<String> = buildList {
    if (user?.has(Protocol.PERMISSION_MANAGE_SCHEDULE) == true) add("课表")
    if (user?.has(Protocol.PERMISSION_MANAGE_SCHEDULE) == true) add("换课")
    if (user?.has(Protocol.PERMISSION_SEND_NOTIFICATIONS) == true ||
        user?.has(Protocol.PERMISSION_SYSTEM_CONTROL) == true) add("控制")
    add("设置")
}

internal fun canViewExtendedSchedule(user: UserProfile?): Boolean =
    user?.has(Protocol.PERMISSION_MANAGE_SCHEDULE) == true

@Composable
private fun HomeInfoChip(
    text: String,
    icon: ImageVector? = null,
    filled: Boolean,
    modifier: Modifier = Modifier.height(24.dp),
    fontSize: TextUnit = if (filled) 11.sp else 12.sp,
    iconSize: Dp = 16.dp,
    onClick: (() -> Unit)? = null,
) {
    Row(
        modifier = modifier.clip(RoundedCornerShape(12.dp))
            .then(
                if (filled) Modifier.background(palette.buttonContainer)
                else Modifier.border(1.dp, Color(0xFF79747E), RoundedCornerShape(12.dp)),
            )
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(horizontal = 7.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Icon(icon, null, tint = palette.onButtonContainer, modifier = Modifier.size(iconSize))
            Spacer(Modifier.width(5.dp))
        }
        Text(
            text,
            color = if (filled) palette.onButtonContainer else Color.Black,
            fontSize = fontSize,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun LessonSummary(course: CourseEntry) {
    Row(
        modifier = Modifier.fillMaxWidth(.82f).height(38.dp).clip(RoundedCornerShape(14.dp))
            .background(palette.buttonContainer).padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(course.label, color = palette.onButtonContainer, fontSize = 11.sp)
        Spacer(Modifier.width(7.dp))
        Text(course.subject, color = palette.onButtonContainer, fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f), maxLines = 1)
        Text(listOfNotNull(course.startTime, course.endTime).joinToString("-"), color = palette.onButtonContainer, fontSize = 9.sp)
    }
}

@Composable
private fun LessonButton(label: String, subject: String, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth(.78f).height(44.dp).clip(RoundedCornerShape(12.dp)).clickable(onClick = onClick)) {
        Box(Modifier.weight(.3f).fillMaxSize().background(palette.secondaryContainer), contentAlignment = Alignment.Center) { Text(label, color = Color.Black, fontWeight = FontWeight.Bold) }
        Box(Modifier.weight(.7f).fillMaxSize().background(palette.primaryContainer), contentAlignment = Alignment.Center) { Text(subject, color = Color.Black, fontWeight = FontWeight.Bold, maxLines = 1) }
    }
}

@Composable
private fun ModeSelector(mode: SwapMode, onModeChange: (SwapMode) -> Unit) {
    Row(Modifier.width(120.dp).height(28.dp).clip(RoundedCornerShape(14.dp)).background(palette.disabledContainer)) {
        listOf(SwapMode.Exchange to "交换", SwapMode.Replace to "替换").forEach { (value, label) ->
            Box(
                Modifier.weight(1f).fillMaxSize().background(if (mode == value) palette.buttonContainer else Color.Transparent).clickable { onModeChange(value) },
                contentAlignment = Alignment.Center,
            ) { Text(label, color = if (mode == value) palette.onButtonContainer else Color.White, fontSize = 11.sp) }
        }
    }
}

@Composable
private fun Input(value: String, onValueChange: (String) -> Unit, label: String, password: Boolean = false) {
    TextField(
        value = value,
        onValueChange = onValueChange,
        modifier = Modifier.fillMaxWidth(.86f),
        label = { androidx.compose.material3.Text(label) },
        singleLine = true,
        visualTransformation = if (password) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
        colors = TextFieldDefaults.colors(
            focusedTextColor = Color.White, unfocusedTextColor = Color.White,
            focusedContainerColor = palette.disabledContainer, unfocusedContainerColor = palette.disabledContainer,
            focusedIndicatorColor = palette.buttonContainer, cursorColor = palette.buttonContainer,
        ),
    )
}

@Composable
private fun Toggle(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    ToggleChip(
        checked = checked,
        onCheckedChange = onChange,
        label = { Text(label) },
        toggleControl = { Switch(checked = checked) },
        colors = ToggleChipDefaults.toggleChipColors(uncheckedStartBackgroundColor = palette.disabledContainer, uncheckedEndBackgroundColor = palette.disabledContainer),
        modifier = Modifier.fillMaxWidth(.88f),
    )
}

@Composable private fun Hint(text: String, onClick: (() -> Unit)? = null) = Text(
    text,
    color = Color.White.copy(.68f),
    fontSize = 10.sp,
    textAlign = TextAlign.Center,
    modifier = Modifier.fillMaxWidth(.76f).then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier),
)
@Composable private fun SectionLabel(text: String) = Text(text, color = palette.buttonContainer, fontSize = 12.sp, fontWeight = FontWeight.Bold)

private fun describeConnectionForScreen(state: ConnectionManager.State): String = when (state) {
    ConnectionManager.State.Idle -> "未连接"
    ConnectionManager.State.Connecting -> "连接中…"
    ConnectionManager.State.LanConnected -> "局域网直连"
    ConnectionManager.State.CloudConnected -> "云端中转"
    is ConnectionManager.State.Error -> state.message
}

@Composable
private fun connectionIndicatorColor(state: ConnectionManager.State): Color = when (state) {
    ConnectionManager.State.LanConnected -> Color(0xFF2E7D32)
    ConnectionManager.State.CloudConnected -> palette.progressActive
    ConnectionManager.State.Connecting -> Color(0xFFF9A825)
    is ConnectionManager.State.Error -> Color(0xFFBA1A1A)
    ConnectionManager.State.Idle -> Color(0xFFC8C4D0)
}

/** 放学后以明天作为浏览和换课下限，防止继续展示已经结束的当天课表。 */
internal fun availableScheduleDays(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate = LocalDate.now(),
): List<ScheduleDay> {
    val firstVisibleDate = if (afterSchool) today.plusDays(1) else today
    return bundle?.days.orEmpty()
        .filter { day -> parseScheduleDate(day.date)?.isBefore(firstVisibleDate) != true }
        .sortedBy { day -> parseScheduleDate(day.date) ?: LocalDate.MAX }
}

internal fun initialScheduleDate(
    bundle: ScheduleBundle?,
    afterSchool: Boolean,
    today: LocalDate = LocalDate.now(),
): String? = availableScheduleDays(bundle, afterSchool, today).firstOrNull()?.date

internal fun scheduleDateTitle(date: String, today: LocalDate = LocalDate.now()): String {
    val suffix = when (parseScheduleDate(date)) {
        today -> "-今天"
        today.plusDays(1) -> "-明天"
        else -> ""
    }
    return "${date.takeLast(5)}$suffix"
}

private fun parseScheduleDate(date: String): LocalDate? = runCatching { LocalDate.parse(date) }.getOrNull()

internal fun buildLessonChoices(day: ScheduleDay?): List<LessonChoice> = day?.courses?.map { it.toChoice() }.orEmpty()

/** 保留当前课程映射测试，产品换课路径使用上面的 ScheduleDay 重载。 */
internal fun buildLessonChoices(snapshot: ClassStateSnapshot?): List<LessonChoice> = listOf(
    LessonChoice("current", 0, extractPeriod(snapshot?.currentTimeLayoutItem) ?: "当前课", snapshot?.currentSubject ?: "未加载课表", extractTimeRange(snapshot?.currentTimeLayoutItem), extractPeriod(snapshot?.currentTimeLayoutItem) ?: "当前课", !snapshot?.currentSubject.isNullOrBlank()),
    LessonChoice("next", 1, extractPeriod(snapshot?.nextClassTimeLayoutItem) ?: "下一节", snapshot?.nextClassSubject ?: "暂无下一节", extractTimeRange(snapshot?.nextClassTimeLayoutItem), extractPeriod(snapshot?.nextClassTimeLayoutItem) ?: "下一节", !snapshot?.nextClassSubject.isNullOrBlank()),
)

private fun CourseEntry.toChoice() = LessonChoice(
    id = index.toString(), index = index, periodLabel = label, subject = subject,
    time = listOfNotNull(startTime, endTime).joinToString("-"), commandValue = index.toString(), enabled = enabled,
)

private val TimeRangeRegex = Regex("(\\d{1,2}:\\d{2})\\s*[-–—~至]\\s*(\\d{1,2}:\\d{2})")
private val LessonPeriodRegex = Regex("第[一二三四五六七八九十\\d]+节")
private fun extractPeriod(value: String?): String? = value?.let(LessonPeriodRegex::find)?.value
internal fun extractTimeRange(value: String?): String {
    val match = value?.let(TimeRangeRegex::find) ?: return ""
    return "${match.groupValues[1]}-${match.groupValues[2]}"
}

/**
 * 把当前时间段（如 "16:30-17:10 语文"）匹配到当天课表的课程零基索引，
 * 供主界面课程按钮“点击快速选中该课换课”预选源课使用。
 * 匹配不到（课表未同步、当前无课或时间段不一致）时返回 null。
 */
internal fun currentLessonIndex(day: ScheduleDay?, value: String?): Int? {
    val range = extractTimeRange(value)
    if (range.isBlank() || day == null) return null
    val parts = range.split("-")
    if (parts.size != 2) return null
    val (start, end) = parts
    return day.courses.firstOrNull { it.enabled && it.startTime == start && it.endTime == end }?.index
}

internal data class HomeCourseContent(
    val subject: String,
    val timeLayoutItem: String?,
    val targetsNextLesson: Boolean,
)

/**
 * 决定主页主课程按钮代表当前课还是下一节课。
 * 下课/即将上课阶段没有正在进行的课程，因此按钮直接承载下一节课及其快速换课入口。
 */
internal fun homeCourseContent(snapshot: ClassStateSnapshot?): HomeCourseContent {
    val targetsNextLesson = snapshot?.currentState in setOf(
        Protocol.STATE_BREAKING,
        Protocol.STATE_PREPARE_CLASS,
    )
    return if (targetsNextLesson) {
        HomeCourseContent(
            subject = snapshot?.nextClassSubject ?: "暂无下一节",
            timeLayoutItem = snapshot?.nextClassTimeLayoutItem,
            targetsNextLesson = true,
        )
    } else {
        HomeCourseContent(
            subject = snapshot?.currentSubject ?: "暂无课程",
            timeLayoutItem = snapshot?.currentTimeLayoutItem,
            targetsNextLesson = false,
        )
    }
}

/** 主按钮已经显示下一节课时，不再在页面底部重复显示“下一节课是”。 */
internal fun shouldShowNextLessonSummary(currentState: Int?): Boolean =
    currentState !in setOf(Protocol.STATE_BREAKING, Protocol.STATE_PREPARE_CLASS)

/** 快速换课预选项必须与主页按钮当前显示的课程一致。 */
internal fun homeQuickSwapLessonIndex(day: ScheduleDay?, snapshot: ClassStateSnapshot?): Int? =
    currentLessonIndex(day, homeCourseContent(snapshot).timeLayoutItem)

/** 只有具有明确起止时间的课程阶段才显示环状进度，避免放学等开放状态产生伪进度。 */
internal fun shouldShowStateProgress(snapshot: ClassStateSnapshot?): Boolean =
    snapshot?.currentState in setOf(
        Protocol.STATE_CLASS,
        Protocol.STATE_PREPARE_CLASS,
        Protocol.STATE_BREAKING,
    ) && extractTimeRange(snapshot?.currentTimeLayoutItem).isNotBlank()

internal fun lessonProgress(value: String?, now: LocalTime): Float {
    val match = value?.let(TimeRangeRegex::find) ?: return 0f
    val start = runCatching { LocalTime.parse(match.groupValues[1]) }.getOrNull() ?: return 0f
    val end = runCatching { LocalTime.parse(match.groupValues[2]) }.getOrNull() ?: return 0f
    val total = Duration.between(start, end).seconds
    return if (total <= 0) 0f else (Duration.between(start, now).seconds.toFloat() / total).coerceIn(0f, 1f)
}

/**
 * 根据插件快照推算“插件本地当前时间”：
 * 以快照的 UTC 生成时间为基准，加上插件时区偏移和本机经过的真实时间，
 * 即使手表时区与插件不一致，课程进度也能按插件时间轴正确计算。
 *
 * @param generatedAt 快照的 UTC 生成时间（ISO-8601，如 "2026-08-12T08:46:55+00:00"）。
 * @param offsetMinutes 插件本地时区相对 UTC 的偏移分钟数；为 null 表示旧版插件无偏移信息。
 * @param baseElapsedMs 收到当前快照时本机 elapsedRealtime 毫秒数。
 * @param nowElapsedMs 当前本机 elapsedRealtime 毫秒数。
 */
internal fun pluginLocalNow(
    generatedAt: String?,
    offsetMinutes: Int?,
    baseElapsedMs: Long,
    nowElapsedMs: Long,
): LocalTime {
    val offset = offsetMinutes?.let { runCatching { ZoneOffset.ofTotalSeconds(it * 60) }.getOrNull() }
    if (offset == null) return LocalTime.now()
    val base = generatedAt?.let { raw ->
        runCatching { OffsetDateTime.parse(raw).toInstant().atOffset(offset).toLocalTime() }.getOrNull()
    } ?: LocalTime.now(offset)
    return base.plusNanos((nowElapsedMs - baseElapsedMs) * 1_000_000L)
}
