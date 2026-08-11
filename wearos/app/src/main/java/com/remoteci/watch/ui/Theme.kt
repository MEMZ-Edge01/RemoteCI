package com.remoteci.watch.ui

import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.wear.compose.material.MaterialTheme

/** Wear OS 应用主题：对齐 Figma 的深色圆形画布与 Material 3 淡紫容器色。 */
@Composable
fun AppTheme(content: @Composable () -> Unit) {
    val colors = MaterialTheme.colors.copy(
        primary = Color(0xFFE8DEF8),
        primaryVariant = Color(0xFFD8D4FF),
        secondary = Color(0xFFFFE1FA),
        secondaryVariant = Color(0xFFD8D4FF),
        background = Color(0xFF1D1D1D),
        surface = Color(0xFF1D1D1D),
        onPrimary = Color(0xFF4A4459),
        onSecondary = Color.Black,
        onBackground = Color.White,
        onSurface = Color.White,
    )
    MaterialTheme(colors = colors, content = content)
}
