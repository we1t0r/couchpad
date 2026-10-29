package app.remotedesk.ui

import androidx.compose.runtime.Composable

/** Системная кнопка или жест «назад». */
@Composable
expect fun BackHandler(onBack: () -> Unit)
