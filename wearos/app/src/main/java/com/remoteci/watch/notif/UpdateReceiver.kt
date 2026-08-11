package com.remoteci.watch.notif

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.remoteci.watch.R

/**
 * 接收 PackageInstaller 的安装结果并提示用户。
 * Manifest 中声明为导出的系统结果接收器，只响应本应用的 action。
 */
class UpdateReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        val success = status == PackageInstaller.STATUS_SUCCESS
        val title = if (success) "更新完成" else "更新失败"
        val text = when {
            success -> "RemoteCI 已更新到新版本，请重新打开应用。"
            !message.isNullOrBlank() -> message
            else -> "安装未完成，请稍后重试。"
        }
        val notification = NotificationCompat.Builder(context, NotificationHelper.CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(title)
            .setContentText(text)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()
        try {
            NotificationManagerCompat.from(context).notify(UPDATE_NOTIFICATION_ID, notification)
        } catch (_: SecurityException) {
            // 未授予通知权限时静默失败，安装结果仍以系统安装页为准。
        }
    }

    private companion object {
        const val UPDATE_NOTIFICATION_ID = 2026
    }
}
