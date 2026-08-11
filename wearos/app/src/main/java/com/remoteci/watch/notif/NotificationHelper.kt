package com.remoteci.watch.notif

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.remoteci.watch.R
import com.remoteci.watch.data.ClassEvent
import com.remoteci.watch.data.EventHistory
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.WatchSettings
import com.remoteci.watch.data.receives

/**
 * 通知+振动助手：课程事件到达时发系统通知并振动。
 */
object NotificationHelper {
    private const val CHANNEL_ID = "remoteci_class"

    fun ensureChannel(context: Context) {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "RemoteCI 通知",
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = "课程、自动化和 ClassIsland 插件提醒"
            enableVibration(true)
        }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    fun handle(context: Context, event: ClassEvent, settings: WatchSettings, history: EventHistory) {
        if (!history.markIfNew(event) || !settings.receives(event)) return
        // Android 13+ 需要通知权限
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            return
        }

        val title = when (event.event) {
            Protocol.EVENT_ON_CLASS -> "上课了"
            Protocol.EVENT_ON_BREAKING -> "课间休息"
            Protocol.EVENT_AFTER_SCHOOL -> "放学啦"
            Protocol.EVENT_SCHEDULE_CHANGED -> "课表已更新"
            Protocol.EVENT_CUSTOM -> event.subject ?: "RemoteCI 通知"
            Protocol.EVENT_AUTOMATION_NOTIFICATION,
            Protocol.EVENT_PLUGIN_NOTIFICATION -> event.subject ?: "ClassIsland 通知"
            else -> "RemoteCI"
        }

        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(title)
            .setContentText(event.message ?: event.subject ?: "")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()

        NotificationManagerCompat.from(context).notify(event.id.ifBlank { event.message.orEmpty() }.hashCode(), notification)
        vibrate(context)
    }

    private fun vibrate(context: Context) {
        val vibrator = if (Build.VERSION.SDK_INT >= 31) {
            context.getSystemService(Vibrator::class.java)
        } else {
            @Suppress("DEPRECATION")
            context.getSystemService(Context.VIBRATOR_SERVICE) as Vibrator
        }
        vibrator.vibrate(VibrationEffect.createOneShot(800, VibrationEffect.DEFAULT_AMPLITUDE))
    }
}
