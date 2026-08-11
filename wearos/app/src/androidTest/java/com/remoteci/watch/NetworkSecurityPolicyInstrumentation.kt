package com.remoteci.watch

import android.app.Activity
import android.app.Instrumentation
import android.os.Bundle
import android.security.NetworkSecurityPolicy

/**
 * 在目标应用进程中读取 Android 网络安全策略，不依赖额外测试框架。
 * 命令输出的 cleartextPermitted 字段可作为局域网 ws:// 回归测试信号。
 */
class NetworkSecurityPolicyInstrumentation : Instrumentation() {
    override fun onCreate(arguments: Bundle?) {
        super.onCreate(arguments)
        start()
    }

    override fun onStart() {
        val permitted = NetworkSecurityPolicy.getInstance()
            .isCleartextTrafficPermitted(LAN_TEST_HOST)
        finish(
            if (permitted) Activity.RESULT_OK else Activity.RESULT_CANCELED,
            Bundle().apply {
                putString("cleartextPermitted", permitted.toString())
                putString("testedHost", LAN_TEST_HOST)
            },
        )
    }

    private companion object {
        const val LAN_TEST_HOST = "192.168.1.100"
    }
}
