package app.remotedesk.ui

import androidx.compose.runtime.Composable

/** Системная кнопка или жест «назад». */
@Composable
expect fun BackHandler(onBack: () -> Unit)

/** Не гасить экран, пока открыт пульт. */
@Composable
expect fun KeepScreenOn(enabled: Boolean)

/** Экран ПК во весь экран телефона: повернуть горизонтально и спрятать системные панели. */
@Composable
expect fun FullScreenMode(enabled: Boolean)

/** Видео экрана ПК (H.264) — на Android аппаратным декодером. На JVM видео нет: там экран идёт картинками. */
@androidx.compose.runtime.Composable
internal expect fun VideoSurface(connection: app.remotedesk.core.AgentConnection, modifier: androidx.compose.ui.Modifier)
