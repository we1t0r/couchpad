package app.remotedesk.ui

import androidx.compose.runtime.Composable

/** Системная кнопка или жест «назад». */
@Composable
expect fun BackHandler(onBack: () -> Unit)

/** Не гасить экран, пока открыт пульт. */
@Composable
expect fun KeepScreenOn(enabled: Boolean)
