package com.remoteci.watch.data

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class UpdateManagerTest {
    @Test
    fun `versionFromTag strips v prefix`() {
        assertEquals("0.2.0", UpdateManager.versionFromTag("v0.2.0"))
        assertEquals("0.2.0", UpdateManager.versionFromTag("0.2.0"))
    }

    @Test
    fun `compareVersions follows semver ordering`() {
        assertTrue(UpdateManager.isNewer("0.3.0", "0.2.0"))
        assertTrue(UpdateManager.isNewer("1.0.0", "0.9.9"))
        assertTrue(UpdateManager.isNewer("0.10.0", "0.9.0"))
        assertFalse(UpdateManager.isNewer("0.2.0", "0.2.0"))
        assertFalse(UpdateManager.isNewer("0.2.0", "0.3.0"))
    }

    @Test
    fun `findApkAsset picks the watch apk only`() {
        val release = GitHubRelease(
            tagName = "v0.2.0",
            assets = listOf(
                GitHubAsset(name = "RemoteCI.Server-0.2.0-linux-x64.zip", browserDownloadUrl = "https://example/server.zip"),
                GitHubAsset(name = "RemoteCI.Watch-0.2.0.apk", browserDownloadUrl = "https://example/watch.apk"),
            ),
        )

        assertEquals("RemoteCI.Watch-0.2.0.apk", UpdateManager.findApkAsset(release)?.name)
    }

    @Test
    fun `findApkAsset returns null when missing`() {
        val release = GitHubRelease(
            tagName = "v0.2.0",
            assets = listOf(
                GitHubAsset(name = "RemoteCI.Plugin-0.2.0.cipx", browserDownloadUrl = "https://example/plugin.cipx"),
            ),
        )

        assertNull(UpdateManager.findApkAsset(release))
    }
}
