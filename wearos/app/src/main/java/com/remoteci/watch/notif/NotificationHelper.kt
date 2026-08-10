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
import com.remoteci.watch.data.Protocol

/**
 * 通知+振动助手：课程事件到达时发系统通知并振动。
 */
object NotificationHelper {
    private const val CHANNEL_ID = "remoteci_class"

    fun ensureChannel(context: Context) {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "课表提醒",
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = "上课、下课、放学提醒"
            enableVibration(true)
        }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    fun notify(context: Context, event: ClassEvent) {
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
            else -> "课表更新"
        }

        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(title)
            .setContentText(event.message ?: event.subject ?: "")
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()

        NotificationManagerCompat.from(context).notify(event.event, notification)
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
