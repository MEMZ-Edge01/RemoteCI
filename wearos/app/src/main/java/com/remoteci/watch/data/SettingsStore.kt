package com.remoteci.watch.data

import android.content.Context

/**
 * 手表端设置（SharedPreferences 持久化）。
 * 默认云端地址为 10.0.2.2：这是 Android 模拟器访问宿主机的固定地址，
 * 便于本地开发；真机使用时改为局域网/NAS 地址。
 */
data class WatchSettings(
    val pairCode: String = "remoteci-demo",
    val cloudServerUrl: String = "http://10.0.2.2:8080",
    val lanHost: String = "",
    val lanPort: Int = 8765,
)

class SettingsStore(context: Context) {
    private val prefs = context.getSharedPreferences("remoteci", Context.MODE_PRIVATE)

    fun load(): WatchSettings = WatchSettings(
        pairCode = prefs.getString(KEY_PAIR_CODE, "remoteci-demo") ?: "remoteci-demo",
        cloudServerUrl = prefs.getString(KEY_CLOUD_URL, "http://10.0.2.2:8080") ?: "http://10.0.2.2:8080",
        lanHost = prefs.getString(KEY_LAN_HOST, "") ?: "",
        lanPort = prefs.getInt(KEY_LAN_PORT, 8765),
    )

    fun save(settings: WatchSettings) {
        prefs.edit()
            .putString(KEY_PAIR_CODE, settings.pairCode)
            .putString(KEY_CLOUD_URL, settings.cloudServerUrl)
            .putString(KEY_LAN_HOST, settings.lanHost)
            .putInt(KEY_LAN_PORT, settings.lanPort)
            .apply()
    }

    private companion object {
        const val KEY_PAIR_CODE = "pairCode"
        const val KEY_CLOUD_URL = "cloudServerUrl"
        const val KEY_LAN_HOST = "lanHost"
        const val KEY_LAN_PORT = "lanPort"
    }
}
