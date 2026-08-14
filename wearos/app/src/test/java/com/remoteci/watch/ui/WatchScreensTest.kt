package com.remoteci.watch.ui

import com.remoteci.watch.data.ClassStateSnapshot
import com.remoteci.watch.data.CourseEntry
import com.remoteci.watch.data.ExtensionDefinition
import com.remoteci.watch.data.ExtensionParameter
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.ScheduleBundle
import com.remoteci.watch.data.ScheduleDay
import com.remoteci.watch.data.UserProfile
import java.time.LocalDate
import java.time.LocalTime
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.School
import androidx.compose.ui.unit.dp
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class WatchScreensTest {
    @Test
    fun `cloud disable toggle belongs only to developer settings`() {
        assertFalse(shouldShowCloudConnectionToggle(developerSettings = false))
        assertTrue(shouldShowCloudConnectionToggle(developerSettings = true))
    }

    @Test
    fun `lan disable toggle belongs only to developer settings`() {
        assertFalse(shouldShowLanConnectionToggle(developerSettings = false))
        assertTrue(shouldShowLanConnectionToggle(developerSettings = true))
    }

    @Test
    fun `lan port accepts only tcp port range`() {
        assertEquals(9123, parseLanPort("9123"))
        assertNull(parseLanPort("0"))
        assertNull(parseLanPort("65536"))
        assertNull(parseLanPort("abc"))
    }

    @Test
    fun `empty schedule offers pull only while connected`() {
        assertTrue(shouldOfferSchedulePull(day = null, connectionReady = true))
        assertFalse(shouldOfferSchedulePull(day = null, connectionReady = false))
        assertFalse(
            shouldOfferSchedulePull(
                day = ScheduleDay("2026-08-13", "revision", enabled = true),
                connectionReady = true,
            ),
        )
    }

    @Test
    fun `rectangular watch surface fills the entire screen without circle clipping`() {
        val layout = calculateWatchSurfaceLayout(
            maxWidth = 201.dp,
            maxHeight = 238.dp,
            isScreenRound = false,
        )

        assertEquals(201.dp, layout.width)
        assertEquals(238.dp, layout.height)
        assertEquals(201.dp, layout.scale)
        assertFalse(layout.clipToCircle)
    }

    @Test
    fun `round watch surface keeps a square circle canvas`() {
        val layout = calculateWatchSurfaceLayout(
            maxWidth = 201.dp,
            maxHeight = 238.dp,
            isScreenRound = true,
        )

        assertEquals(201.dp, layout.width)
        assertEquals(201.dp, layout.height)
        assertEquals(201.dp, layout.scale)
        assertTrue(layout.clipToCircle)
    }

    @Test
    fun `extractTimeRange keeps the normalized range`() {
        assertEquals("13:00-14:00", extractTimeRange("13:00 至 14:00（第三节）"))
    }

    @Test
    fun `home time chip can grow for long ranges without exceeding the safe width`() {
        val bounds = homeTimeChipWidthBounds(200.dp)

        assertEquals(78.dp, bounds.minWidth)
        assertEquals(144.dp, bounds.maxWidth)
        assertTrue(bounds.maxWidth > bounds.minWidth)
    }

    @Test
    fun `lessonProgress returns the midpoint of a lesson`() {
        val progress = lessonProgress("13:00-14:00", LocalTime.of(13, 30))

        assertEquals(0.5f, progress)
    }

    @Test
    fun `lessonProgress matches real plugin payload with subject suffix`() {
        // 插件实际推送格式："16:30-17:10 语文"
        val progress = lessonProgress("16:30-17:10 语文", LocalTime.of(16, 46))

        assertEquals(0.4f, progress, 0.001f)
        assertEquals("16:30-17:10", extractTimeRange("16:30-17:10 语文"))
    }

    @Test
    fun `currentLessonIndex matches today lesson by time range`() {
        val day = ScheduleDay(
            date = "2026-08-12",
            revision = "r1",
            courses = listOf(
                CourseEntry(index = 0, label = "第 1 节", subjectId = "a", subject = "语文", startTime = "16:30", endTime = "17:10", enabled = true),
                CourseEntry(index = 1, label = "第 2 节", subjectId = "b", subject = "数学", startTime = "17:20", endTime = "18:00", enabled = true),
                CourseEntry(index = 2, label = "第 3 节", subjectId = "c", subject = "英语", startTime = "18:10", endTime = "18:50", enabled = false),
            ),
        )

        assertEquals(0, currentLessonIndex(day, "16:30-17:10 语文"))
        assertEquals(1, currentLessonIndex(day, "17:20-18:00 数学"))
        // 禁用的课程不作为换课源课。
        assertNull(currentLessonIndex(day, "18:10-18:50 英语"))
        // 时间不在课表中或缺少课表时匹配不到。
        assertNull(currentLessonIndex(day, "19:00-20:00 晚自习"))
        assertNull(currentLessonIndex(null, "16:30-17:10 语文"))
    }

    @Test
    fun `break and upcoming states put the next lesson in the home course button`() {
        listOf(Protocol.STATE_BREAKING, Protocol.STATE_PREPARE_CLASS).forEach { state ->
            val content = homeCourseContent(
                ClassStateSnapshot(
                    currentState = state,
                    currentSubject = "语文",
                    currentTimeLayoutItem = "16:30-17:10 语文",
                    nextClassSubject = "数学",
                    nextClassTimeLayoutItem = "17:20-18:00 数学",
                ),
            )

            assertEquals("数学", content.subject)
            assertEquals("17:20-18:00 数学", content.timeLayoutItem)
            assertTrue(content.targetsNextLesson)
            assertFalse(shouldShowNextLessonSummary(state))
        }
    }

    @Test
    fun `active class keeps the current lesson and its next lesson summary`() {
        val content = homeCourseContent(
            ClassStateSnapshot(
                currentState = Protocol.STATE_CLASS,
                currentSubject = "语文",
                currentTimeLayoutItem = "16:30-17:10 语文",
                nextClassSubject = "数学",
                nextClassTimeLayoutItem = "17:20-18:00 数学",
            ),
        )

        assertEquals("语文", content.subject)
        assertEquals("16:30-17:10 语文", content.timeLayoutItem)
        assertFalse(content.targetsNextLesson)
        assertTrue(shouldShowNextLessonSummary(Protocol.STATE_CLASS))
    }

    @Test
    fun `home quick swap selects the lesson shown in the course button`() {
        val day = ScheduleDay(
            date = "2026-08-12",
            revision = "r1",
            courses = listOf(
                CourseEntry(index = 0, label = "第 1 节", subjectId = "a", subject = "语文", startTime = "16:30", endTime = "17:10"),
                CourseEntry(index = 1, label = "第 2 节", subjectId = "b", subject = "数学", startTime = "17:20", endTime = "18:00"),
            ),
        )
        val upcoming = ClassStateSnapshot(
            currentState = Protocol.STATE_PREPARE_CLASS,
            currentTimeLayoutItem = "16:30-17:10 语文",
            nextClassTimeLayoutItem = "17:20-18:00 数学",
        )
        val inClass = upcoming.copy(currentState = Protocol.STATE_CLASS)

        assertEquals(1, homeQuickSwapLessonIndex(day, upcoming))
        assertEquals(0, homeQuickSwapLessonIndex(day, inClass))
    }

    @Test
    fun `pluginLocalNow aligns progress with plugin timezone`() {
        // 插件在 UTC+8，快照生成于 08:46:55Z，本地应为 16:46:55。
        val atSnapshot = pluginLocalNow("2026-08-12T08:46:55.0573662+00:00", 480, 1_000L, 1_000L)
        assertEquals(LocalTime.of(16, 46, 55), atSnapshot.withNano(0))

        // 两分钟后的插件本地时间。
        val twoMinutesLater = pluginLocalNow("2026-08-12T08:46:55.0573662+00:00", 480, 1_000L, 121_000L)
        assertEquals(LocalTime.of(16, 48, 55), twoMinutesLater.withNano(0))
    }

    @Test
    fun `pluginLocalNow falls back to device local time without offset`() {
        // 旧版插件不携带偏移信息时保持原行为，不抛异常。
        val fallback = pluginLocalNow(null, null, 0L, 0L)
        assertTrue(fallback.isBefore(LocalTime.now().plusMinutes(1)))
    }

    @Test
    fun `progress ring only appears for bounded course phases`() {
        val timed = "13:00-14:00"

        assertTrue(shouldShowStateProgress(ClassStateSnapshot(currentState = Protocol.STATE_CLASS, currentTimeLayoutItem = timed)))
        assertTrue(shouldShowStateProgress(ClassStateSnapshot(currentState = Protocol.STATE_PREPARE_CLASS, currentTimeLayoutItem = timed)))
        assertTrue(shouldShowStateProgress(ClassStateSnapshot(currentState = Protocol.STATE_BREAKING, currentTimeLayoutItem = timed)))
        assertFalse(shouldShowStateProgress(ClassStateSnapshot(currentState = Protocol.STATE_AFTER_SCHOOL, currentTimeLayoutItem = timed)))
        assertFalse(shouldShowStateProgress(ClassStateSnapshot(currentState = Protocol.STATE_CLASS, currentTimeLayoutItem = null)))
    }

    @Test
    fun `course state labels distinguish preparation and dismissal`() {
        assertEquals("即将上课", describeClassState(ClassStateSnapshot(currentState = Protocol.STATE_PREPARE_CLASS)))
        assertEquals("下课", describeClassState(ClassStateSnapshot(currentState = Protocol.STATE_BREAKING)))
        assertEquals("放学", describeClassState(ClassStateSnapshot(currentState = Protocol.STATE_AFTER_SCHOOL)))
    }

    @Test
    fun `buildLessonChoices uses live subjects and period identifiers`() {
        val choices = buildLessonChoices(
            ClassStateSnapshot(
                currentSubject = "数学",
                nextClassSubject = "物理",
                currentTimeLayoutItem = "13:00-14:00（第三节）",
                nextClassTimeLayoutItem = "14:00-15:00（第四节）",
            ),
        )

        assertEquals("第三节", choices[0].commandValue)
        assertEquals("数学", choices[0].subject)
        assertTrue(choices[0].enabled)
        assertEquals("第四节", choices[1].commandValue)
        assertEquals("物理", choices[1].subject)
        assertTrue(choices[1].enabled)
    }

    @Test
    fun `buildLessonChoices disables unavailable schedule entries`() {
        val choices = buildLessonChoices(null as ClassStateSnapshot?)

        assertFalse(choices[0].enabled)
        assertFalse(choices[1].enabled)
    }

    @Test
    fun `home actions follow effective permissions`() {
        assertEquals(listOf("设置"), homeActionLabels(UserProfile(permissions = Protocol.PERMISSION_VIEW_CURRENT)))
        assertEquals(
            listOf("课表", "换课", "设置"),
            homeActionLabels(
                UserProfile(
                    permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_SCHEDULE,
                ),
            ),
        )
        assertEquals(
            listOf("课表", "换课", "控制", "设置"),
            homeActionLabels(UserProfile(permissions = 31)),
        )
        assertEquals(
            listOf("控制", "设置"),
            homeActionLabels(
                UserProfile(
                    permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_SYSTEM_CONTROL,
                ),
            ),
        )
    }

    @Test
    fun `control state decides clear and main menu labels`() {
        assertFalse(shouldShowClearNotifications(ClassStateSnapshot(isNotificationPlaying = false)))
        assertTrue(shouldShowClearNotifications(ClassStateSnapshot(isNotificationPlaying = true)))
        assertEquals("隐藏主菜单", mainMenuActionLabel(ClassStateSnapshot(isMainMenuVisible = true)))
        assertEquals("显示主菜单", mainMenuActionLabel(ClassStateSnapshot(isMainMenuVisible = false)))
    }

    @Test
    fun `rotary volume follows Wear OS direction and clamps`() {
        assertEquals(52, adjustVolumeForRotary(50, 1f))
        assertEquals(48, adjustVolumeForRotary(50, -1f))
        assertEquals(100, adjustVolumeForRotary(100, 1f))
        assertEquals(0, adjustVolumeForRotary(0, -1f))
    }

    @Test
    fun `only schedule managers can see next class details`() {
        assertFalse(canViewExtendedSchedule(UserProfile(permissions = Protocol.PERMISSION_VIEW_CURRENT)))
        assertTrue(
            canViewExtendedSchedule(
                UserProfile(
                    permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_MANAGE_SCHEDULE,
                ),
            ),
        )
    }

    @Test
    fun `schedule date title marks today and tomorrow`() {
        val today = LocalDate.of(2026, 8, 12)

        assertEquals("08-12-今天", scheduleDateTitle("2026-08-12", today))
        assertEquals("08-13-明天", scheduleDateTitle("2026-08-13", today))
        assertEquals("08-14", scheduleDateTitle("2026-08-14", today))
    }

    @Test
    fun `after school schedule starts tomorrow and excludes today`() {
        val today = LocalDate.of(2026, 8, 12)
        val bundle = ScheduleBundle(
            days = listOf(
                ScheduleDay("2026-08-12", "today", classPlanName = "默认（临时层）", enabled = true),
                ScheduleDay("2026-08-13", "tomorrow", enabled = true),
                ScheduleDay("2026-08-14", "later", enabled = true),
            ),
        )

        assertEquals(listOf("2026-08-13", "2026-08-14"), availableScheduleDays(bundle, true, today).map { it.date })
        assertEquals("2026-08-13", initialScheduleDate(bundle, true, today))
        assertFalse(scheduleDateTitle(bundle.days.first().date, today).contains("临时层"))
    }

    @Test
    fun `before dismissal schedule still begins today`() {
        val today = LocalDate.of(2026, 8, 12)
        val bundle = ScheduleBundle(
            days = listOf(
                ScheduleDay("2026-08-12", "today", enabled = true),
                ScheduleDay("2026-08-13", "tomorrow", enabled = true),
            ),
        )

        assertEquals("2026-08-12", initialScheduleDate(bundle, false, today))
    }

    @Test
    fun `extension visibility follows declared permission`() {
        val extension = ExtensionDefinition(
            id = "demo.lock",
            displayName = "锁屏",
            requiredPermission = Protocol.PERMISSION_SYSTEM_CONTROL,
        )
        val admin = UserProfile(permissions = 63)
        val student = UserProfile(permissions = Protocol.PERMISSION_VIEW_CURRENT)

        assertEquals(listOf(extension), visibleExtensionsFor(admin, listOf(extension)))
        assertTrue(visibleExtensionsFor(student, listOf(extension)).isEmpty())
        assertTrue(visibleExtensionsFor(null, listOf(extension)).isEmpty())
    }

    @Test
    fun `extension icon maps whitelist and falls back for unknown names`() {
        assertEquals(Icons.Rounded.School, extensionIcon("school"))
        assertEquals(Icons.Rounded.School, extensionIcon(" School "))
        assertNull(extensionIcon("unknown-icon"))
        assertNull(extensionIcon(null))
    }

    @Test
    fun `extension form defaults follow parameter schema`() {
        val extension = ExtensionDefinition(
            id = "demo.say",
            displayName = "喊话",
            parameters = listOf(
                ExtensionParameter(key = "message", label = "内容", defaultValue = "你好"),
                ExtensionParameter(
                    key = "urgent",
                    label = "紧急",
                    type = Protocol.EXT_PARAM_SWITCH,
                    defaultValue = "true",
                ),
                ExtensionParameter(key = "silent", label = "静音", type = Protocol.EXT_PARAM_SWITCH),
            ),
        )

        assertEquals(
            mapOf("message" to "你好", "urgent" to "true", "silent" to "false"),
            defaultExtensionArgs(extension),
        )
    }

    @Test
    fun `select parameter cycles options and clamps empty list`() {
        val options = listOf("A", "B", "C")

        assertEquals("B", nextSelectValue(options, "A"))
        assertEquals("A", nextSelectValue(options, "C"))
        assertEquals("A", nextSelectValue(options, null))
        assertEquals("X", nextSelectValue(emptyList(), "X"))
    }
}
