package com.remoteci.watch.data

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class AuthorizationAndNotificationTest {
    @Test
    fun `password login bootstraps cloud even when developer disabled cloud connection`() {
        val settings = WatchSettings(cloudConnectionEnabled = false)

        val plan = planConnection(settings, password = "valid-password")

        assertTrue(plan.bootstrapCloudAuthentication)
        assertFalse(plan.allowCloudFallback)
    }

    @Test
    fun `schedule pull envelope uses dedicated read-only protocol message`() {
        val envelope = schedulePullEnvelope()

        assertEquals(Protocol.TYPE_SCHEDULE_PULL, envelope.type)
        assertTrue(envelope.messageId.isNotBlank())
        assertNull(envelope.payload)
    }

    @Test
    fun `new devices receive every supported event type`() {
        val settings = WatchSettings()

        listOf(
            Protocol.EVENT_ON_CLASS,
            Protocol.EVENT_ON_BREAKING,
            Protocol.EVENT_AFTER_SCHOOL,
            Protocol.EVENT_SCHEDULE_CHANGED,
            Protocol.EVENT_CUSTOM,
            Protocol.EVENT_AUTOMATION_NOTIFICATION,
            Protocol.EVENT_PLUGIN_NOTIFICATION,
        ).forEach { eventType ->
            assertTrue(settings.receives(ClassEvent(id = "event-$eventType", event = eventType)))
        }
    }

    @Test
    fun `event preferences are independent`() {
        val settings = WatchSettings(receiveCustom = false, receiveAutomationNotifications = false)

        assertFalse(settings.receives(ClassEvent(id = "custom", event = Protocol.EVENT_CUSTOM)))
        assertTrue(settings.receives(ClassEvent(id = "class", event = Protocol.EVENT_ON_CLASS)))
        assertFalse(settings.receives(ClassEvent(id = "automation", event = Protocol.EVENT_AUTOMATION_NOTIFICATION)))
        assertTrue(settings.receives(ClassEvent(id = "plugin", event = Protocol.EVENT_PLUGIN_NOTIFICATION)))
    }

    @Test
    fun `effective permission gates privileged features`() {
        val user = UserProfile(
            permissions = Protocol.PERMISSION_VIEW_CURRENT or Protocol.PERMISSION_SEND_NOTIFICATIONS,
        )

        assertTrue(user.has(Protocol.PERMISSION_VIEW_CURRENT))
        assertTrue(user.has(Protocol.PERMISSION_SEND_NOTIFICATIONS))
        assertFalse(user.has(Protocol.PERMISSION_MANAGE_SCHEDULE))
    }
}
