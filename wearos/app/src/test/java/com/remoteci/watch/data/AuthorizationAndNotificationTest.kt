package com.remoteci.watch.data

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class AuthorizationAndNotificationTest {
    @Test
    fun `new devices receive every supported event type`() {
        val settings = WatchSettings()

        listOf(
            Protocol.EVENT_ON_CLASS,
            Protocol.EVENT_ON_BREAKING,
            Protocol.EVENT_AFTER_SCHOOL,
            Protocol.EVENT_SCHEDULE_CHANGED,
            Protocol.EVENT_CUSTOM,
        ).forEach { eventType ->
            assertTrue(settings.receives(ClassEvent(id = "event-$eventType", event = eventType)))
        }
    }

    @Test
    fun `event preferences are independent`() {
        val settings = WatchSettings(receiveCustom = false)

        assertFalse(settings.receives(ClassEvent(id = "custom", event = Protocol.EVENT_CUSTOM)))
        assertTrue(settings.receives(ClassEvent(id = "class", event = Protocol.EVENT_ON_CLASS)))
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
