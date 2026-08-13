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
    fun `compareVersions follows prerelease precedence`() {
        assertTrue(UpdateManager.isNewer("0.4.0-beta.2", "0.4.0-beta.1"))
        assertTrue(UpdateManager.isNewer("0.4.0", "0.4.0-beta.2"))
        assertFalse(UpdateManager.isNewer("0.4.0-beta.2", "0.4.0"))
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

    @Test
    fun `selectCompatibleUpdate never exceeds connected server version`() {
        val releases = listOf(
            release("v0.3.2"),
            release("v0.3.1"),
            release("v0.3.0"),
        )

        val selected = UpdateManager.selectCompatibleUpdate(
            releases = releases,
            currentVersion = "0.2.0",
            serverVersion = "0.3.1",
        )

        assertEquals("v0.3.1", selected?.release?.tagName)
        assertEquals("RemoteCI.Watch-0.3.1.apk", selected?.asset?.name)
    }

    @Test
    fun `selectCompatibleUpdate returns null when server has no newer compatible watch`() {
        val selected = UpdateManager.selectCompatibleUpdate(
            releases = listOf(release("v0.3.2"), release("v0.3.1")),
            currentVersion = "0.3.1",
            serverVersion = "0.3.1",
        )

        assertNull(selected)
    }

    @Test
    fun `selectCompatibleUpdate ignores beta and draft releases`() {
        val selected = UpdateManager.selectCompatibleUpdate(
            releases = listOf(
                release("v0.3.2-beta.1", prerelease = true),
                release("v0.3.2", draft = true),
                release("v0.3.1"),
            ),
            currentVersion = "0.3.0",
            serverVersion = "0.3.2",
        )

        assertEquals("v0.3.1", selected?.release?.tagName)
    }

    @Test
    fun `stable channel excludes prereleases`() {
        val selected = UpdateManager.selectCompatibleUpdate(
            releases = listOf(
                release("v0.4.0-beta.1", prerelease = true),
                release("v0.3.1"),
            ),
            currentVersion = "0.3.0",
            serverVersion = "0.4.0-beta.1",
            channel = UpdateChannel.STABLE,
            force = false,
        )

        assertEquals("v0.3.1", selected?.release?.tagName)
    }

    @Test
    fun `beta channel includes prereleases`() {
        val selected = UpdateManager.selectCompatibleUpdate(
            releases = listOf(
                release("v0.5.0-beta.1", prerelease = true, draft = true),
                release("v0.4.0-beta.1", prerelease = true),
                release("v0.3.1"),
            ),
            currentVersion = "0.3.0",
            serverVersion = "0.4.0-beta.1",
            channel = UpdateChannel.BETA,
            force = false,
        )

        assertEquals("v0.4.0-beta.1", selected?.release?.tagName)
    }

    @Test
    fun `force update allows same version but never downgrades`() {
        val sameVersion = UpdateManager.selectCompatibleUpdate(
            releases = listOf(release("v0.3.1")),
            currentVersion = "0.3.1",
            serverVersion = "0.3.1",
            channel = UpdateChannel.STABLE,
            force = true,
        )
        val downgrade = UpdateManager.selectCompatibleUpdate(
            releases = listOf(release("v0.3.0")),
            currentVersion = "0.3.1",
            serverVersion = "0.3.1",
            channel = UpdateChannel.STABLE,
            force = true,
        )

        assertEquals("v0.3.1", sameVersion?.release?.tagName)
        assertNull(downgrade)
    }

    @Test
    fun `beta channel and force update still respect server ceiling`() {
        val selected = UpdateManager.selectCompatibleUpdate(
            releases = listOf(
                release("v0.4.0"),
                release("v0.4.0-beta.2", prerelease = true),
            ),
            currentVersion = "0.3.1",
            serverVersion = "0.4.0-beta.2",
            channel = UpdateChannel.BETA,
            force = true,
        )

        assertEquals("v0.4.0-beta.2", selected?.release?.tagName)
    }

    private fun release(
        version: String,
        prerelease: Boolean = false,
        draft: Boolean = false,
    ) = GitHubRelease(
        tagName = version,
        prerelease = prerelease,
        draft = draft,
        assets = listOf(
            GitHubAsset(
                name = "RemoteCI.Watch-${version.removePrefix("v")}.apk",
                browserDownloadUrl = "https://example/${version.removePrefix("v")}.apk",
            ),
        ),
    )
}
