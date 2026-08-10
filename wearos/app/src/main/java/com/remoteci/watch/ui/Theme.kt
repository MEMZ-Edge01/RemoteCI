package com.remoteci.watch.ui

import androidx.compose.runtime.Composable
import androidx.wear.compose.material.MaterialTheme

/** Wear OS 应用主题：使用 Wear Material（Material 3 风格），自动适配深浅色。 */
@Composable
fun AppTheme(content: @Composable () -> Unit) {
    MaterialTheme(content = content)
}
