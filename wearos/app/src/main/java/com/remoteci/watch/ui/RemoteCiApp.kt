package com.remoteci.watch.ui

import android.content.Context
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.material3.TextField
import androidx.wear.compose.foundation.lazy.ScalingLazyColumn
import androidx.wear.compose.foundation.lazy.rememberScalingLazyListState
import androidx.wear.compose.material.Button
import androidx.wear.compose.material.MaterialTheme
import androidx.wear.compose.material.Scaffold
import androidx.wear.compose.material.Text
import androidx.wear.compose.material.TimeText
import com.remoteci.watch.data.ClassEvent
import com.remoteci.watch.data.ConnectionManager
import com.remoteci.watch.data.Protocol
import com.remoteci.watch.data.SettingsStore
import com.remoteci.watch.data.WatchSettings
import com.remoteci.watch.notif.NotificationHelper

private enum class Screen { Home, Control, Settings }

/** 应用根：导航 + 三页面 + 事件收集（通知）。 */
@Composable
fun RemoteCiApp(context: Context) {
    val settingsStore = remember { SettingsStore(context) }
    var settings by remember { mutableStateOf(settingsStore.load()) }
    var screen by remember { mutableStateOf(Screen.Home) }

    val connectionState by ConnectionManager.state.collectAsState()
    val snapshot by ConnectionManager.snapshot.collectAsState()
    val commandResult by ConnectionManager.lastCommandResult.collectAsState()

    // 课程事件 → 通知+振动
    LaunchedEffect(Unit) {
        ConnectionManager.events.collect { event: ClassEvent ->
            NotificationHelper.notify(context, event)
        }
    }

    Scaffold(timeText = { TimeText() }) {
        when (screen) {
            Screen.Home -> HomeScreen(
                stateText = describeConnection(connectionState),
                snapshot = snapshot,
                onOpenControl = { screen = Screen.Control },
                onOpenSettings = { screen = Screen.Settings },
            )

            Screen.Control -> ControlScreen(
                resultText = commandResult?.let { if (it.success) "✅ ${it.message}" else "❌ ${it.message}" },
                onBack = { screen = Screen.Home },
            )

            Screen.Settings -> SettingsScreen(
                settings = settings,
                stateText = describeConnection(connectionState),
                onSettingsChange = { settings = it },
                onConnect = { ConnectionManager.connect(settings) },
                onDisconnect = { ConnectionManager.disconnect() },
                onSave = { settingsStore.save(settings) },
                onBack = { screen = Screen.Home },
            )
        }
    }
}

/** 主页：当前课/下一节/倒计时/周次 + 入口按钮。 */
@Composable
private fun HomeScreen(
    stateText: String,
    snapshot: com.remoteci.watch.data.ClassStateSnapshot?,
    onOpenControl: () -> Unit,
    onOpenSettings: () -> Unit,
) {
    ScalingLazyColumn(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        state = rememberScalingLazyListState(),
    ) {
        item {
            Text(
                text = stateText,
                style = MaterialTheme.typography.caption2,
                textAlign = TextAlign.Center,
            )
        }
        item {
            Text(
                text = snapshot?.currentSubject ?: "未加载课表",
                style = MaterialTheme.typography.display1,
                textAlign = TextAlign.Center,
            )
        }
        item {
            Text(
                text = describeCurrentState(snapshot),
                style = MaterialTheme.typography.title3,
                textAlign = TextAlign.Center,
            )
        }
        item {
            Text(
                text = "下一节：${snapshot?.nextClassSubject ?: "无"}\n${snapshot?.nextClassTimeLayoutItem ?: ""}",
                style = MaterialTheme.typography.body2,
                textAlign = TextAlign.Center,
            )
        }
        item {
            Text(
                text = snapshot?.weekRotation?.let { "第 $it 周" } ?: "",
                style = MaterialTheme.typography.body2,
                textAlign = TextAlign.Center,
            )
        }
        item {
            Button(onClick = onOpenControl, modifier = Modifier.fillMaxSize()) {
                Text("控制")
            }
        }
        item {
            Button(onClick = onOpenSettings, modifier = Modifier.fillMaxSize()) {
                Text("设置")
            }
        }
    }
}

/** 控制页：切换周次 + 临时换课。 */
@Composable
private fun ControlScreen(resultText: String?, onBack: () -> Unit) {
    var from by remember { mutableStateOf("") }
    var to by remember { mutableStateOf("") }

    ScalingLazyColumn(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        state = rememberScalingLazyListState(),
    ) {
        item {
            Button(
                onClick = { ConnectionManager.sendCommand(Protocol.CMD_SWITCH_WEEK) },
                modifier = Modifier.fillMaxSize(),
            ) {
                Text("切换周次")
            }
        }
        item { Text("临时换课", style = MaterialTheme.typography.title3) }
        item {
            TextField(
                value = from,
                onValueChange = { from = it },
                placeholder = { Text("源节次，如 第1节") },
                label = { Text("从") },
            )
        }
        item {
            TextField(
                value = to,
                onValueChange = { to = it },
                placeholder = { Text("目标节次，如 第3节") },
                label = { Text("到") },
            )
        }
        item {
            Button(
                onClick = {
                    ConnectionManager.sendCommand(
                        Protocol.CMD_TEMP_SWAP,
                        "from" to from.ifBlank { "?" },
                        "to" to to.ifBlank { "?" },
                    )
                },
                modifier = Modifier.fillMaxSize(),
            ) {
                Text("发送换课")
            }
        }
        item { Text(resultText ?: "", style = MaterialTheme.typography.body2) }
        item {
            Button(onClick = onBack, modifier = Modifier.fillMaxSize()) {
                Text("返回")
            }
        }
    }
}

/** 设置页：配对码/云端地址/局域网 IP + 连接管理。 */
@Composable
private fun SettingsScreen(
    settings: WatchSettings,
    stateText: String,
    onSettingsChange: (WatchSettings) -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onSave: () -> Unit,
    onBack: () -> Unit,
) {
    ScalingLazyColumn(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        state = rememberScalingLazyListState(),
    ) {
        item { Text("连接状态：$stateText", style = MaterialTheme.typography.caption2) }
        item {
            TextField(
                value = settings.pairCode,
                onValueChange = { onSettingsChange(settings.copy(pairCode = it)) },
                placeholder = { Text("配对码") },
                label = { Text("配对码") },
            )
        }
        item {
            TextField(
                value = settings.cloudServerUrl,
                onValueChange = { onSettingsChange(settings.copy(cloudServerUrl = it)) },
                placeholder = { Text("http://10.0.2.2:8080") },
                label = { Text("云端地址") },
            )
        }
        item {
            TextField(
                value = settings.lanHost,
                onValueChange = { onSettingsChange(settings.copy(lanHost = it)) },
                placeholder = { Text("如 192.168.1.100") },
                label = { Text("电脑局域网IP") },
            )
        }
        item {
            Button(
                onClick = {
                    onSave()
                    onConnect()
                },
                modifier = Modifier.fillMaxSize(),
            ) {
                Text("保存并连接")
            }
        }
        item {
            Button(onClick = onDisconnect, modifier = Modifier.fillMaxSize()) {
                Text("断开")
            }
        }
        item {
            Button(onClick = onBack, modifier = Modifier.fillMaxSize()) {
                Text("返回")
            }
        }
    }
}

private fun describeConnection(state: ConnectionManager.State): String = when (state) {
    ConnectionManager.State.Idle -> "未连接"
    ConnectionManager.State.Connecting -> "连接中…"
    ConnectionManager.State.LanConnected -> "局域网直连"
    ConnectionManager.State.CloudConnected -> "云端中转"
    is ConnectionManager.State.Error -> "错误：${state.message}"
}

private fun describeCurrentState(snapshot: com.remoteci.watch.data.ClassStateSnapshot?): String {
    val base = when (snapshot?.currentState) {
        Protocol.STATE_CLASS -> "上课中"
        Protocol.STATE_BREAKING -> "课间休息"
        Protocol.STATE_AFTER_SCHOOL -> "已放学"
        else -> "待机"
    }
    val countdown = snapshot?.onClassLeftTime
        ?: snapshot?.onBreakingLeftTime
    return countdown?.let { "$base · $it" } ?: base
}
