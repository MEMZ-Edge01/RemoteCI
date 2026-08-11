package com.remoteci.watch.ui

import com.remoteci.watch.data.ClassStateSnapshot
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.ScheduleBundle
import com.remoteci.watch.data.ScheduleDay
import com.remoteci.watch.data.UserProfile
import java.time.LocalDate
import java.time.LocalTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class WatchScreensTest {
    @Test
    fun `extractTimeRange keeps the normalized range`() {
        assertEquals("13:00-14:00", extractTimeRange("13:00 至 14:00（第三节）"))
    }

    @Test
    fun `lessonProgress returns the midpoint of a lesson`() {
        val progress = lessonProgress("13:00-14:00", LocalTime.of(13, 30))

        assertEquals(0.5f, progress)
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
        assertEquals("准备上课", describeClassState(ClassStateSnapshot(currentState = Protocol.STATE_PREPARE_CLASS)))
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
}
