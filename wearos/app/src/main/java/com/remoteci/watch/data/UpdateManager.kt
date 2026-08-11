package com.remoteci.watch.data

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request

/** GitHub release 响应中与本应用相关的字段。 */
@Serializable
data class GitHubRelease(
    @SerialName("tag_name") val tagName: String = "",
    @SerialName("name") val name: String? = null,
    @SerialName("body") val body: String? = null,
    @SerialName("assets") val assets: List<GitHubAsset> = emptyList(),
)

@Serializable
data class GitHubAsset(
    @SerialName("name") val name: String = "",
    @SerialName("browser_download_url") val browserDownloadUrl: String = "",
    @SerialName("size") val size: Long = 0,
)

/**
 * 手表端更新入口：从 GitHub 仓库最新 release 拉取 APK 并安装。
 *
 * 安装沿用 [PackageInstaller] 官方机制，要求发布 APK 与当前安装包签名一致，
 * 首次安装正式签名版后后续更新即可在同一签名下自动覆盖。
 */
object UpdateManager {
    private const val REPO = "MEMZ-Edge01/RemoteCI"
    private const val API_LATEST_RELEASE = "https://api.github.com/repos/$REPO/releases/latest"
    private const val USER_AGENT = "RemoteCI-Watch/0.2"
    private const val APK_PREFIX = "RemoteCI.Watch-"
    const val INSTALL_RESULT_ACTION = "com.remoteci.watch.UPDATE_INSTALL_RESULT"

    private val json = Json { ignoreUnknownKeys = true }
    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    /** 拉取最新 release 元数据。 */
    suspend fun fetchLatestRelease(): GitHubRelease = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(API_LATEST_RELEASE)
            .header("User-Agent", USER_AGENT)
            .build()
        okHttp.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("检查更新失败（HTTP ${response.code}）")
            val text = response.body?.string() ?: throw IOException("检查更新失败（响应为空）")
            json.decodeFromString<GitHubRelease>(text)
        }
    }

    /** 在 release 附件中寻找手表 APK。 */
    fun findApkAsset(release: GitHubRelease): GitHubAsset? =
        release.assets.firstOrNull { it.name.startsWith(APK_PREFIX) && it.name.endsWith(".apk") }

    /** 去掉 tag 前缀 v，得到可比较的版本号。 */
    fun versionFromTag(tag: String): String = tag.removePrefix("v").trim()

    /** latest > current 时返回 true。 */
    fun isNewer(latest: String, current: String): Boolean = compareVersions(latest, current) > 0

    /** 简单的语义版本比较，支持 0.2.0 / v0.2.0 / 0.2.0-beta 等形式。 */
    fun compareVersions(a: String, b: String): Int {
        val left = a.removePrefix("v").split("-", "+").first().split(".").mapNotNull { it.toIntOrNull() }
        val right = b.removePrefix("v").split("-", "+").first().split(".").mapNotNull { it.toIntOrNull() }
        val size = maxOf(left.size, right.size)
        for (i in 0 until size) {
            val diff = (left.getOrElse(i) { 0 }) - (right.getOrElse(i) { 0 })
            if (diff != 0) return diff
        }
        return 0
    }

    /** 下载 APK 到缓存目录，返回本地文件。 */
    suspend fun downloadApk(context: Context, asset: GitHubAsset): File = withContext(Dispatchers.IO) {
        val target = File(context.cacheDir, asset.name)
        if (target.exists()) target.delete()
        val request = Request.Builder()
            .url(asset.browserDownloadUrl)
            .header("User-Agent", USER_AGENT)
            .build()
        okHttp.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("下载失败（HTTP ${response.code}）")
            val body = response.body ?: throw IOException("下载失败（响应为空）")
            body.byteStream().use { input ->
                target.outputStream().use { output -> input.copyTo(output) }
            }
        }
        target
    }

    /** 通过系统 PackageInstaller 安装，结果由 UpdateReceiver 接收并提示。 */
    fun installApk(context: Context, apk: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(context.packageName)
        val sessionId = installer.createSession(params)
        val session = installer.openSession(sessionId)
        try {
            session.openWrite(apk.name, 0, apk.length()).use { output ->
                apk.inputStream().use { input -> input.copyTo(output) }
                session.fsync(output)
            }
            val intent = Intent(INSTALL_RESULT_ACTION).setPackage(context.packageName)
            val pending = PendingIntent.getBroadcast(
                context,
                0,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            session.commit(pending.intentSender)
        } finally {
            session.close()
        }
    }
}
