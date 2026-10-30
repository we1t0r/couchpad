package app.remotedesk.ui

import androidx.compose.runtime.Composable

@Composable
actual fun BackHandler(onBack: () -> Unit) = Unit

@Composable
actual fun KeepScreenOn(enabled: Boolean) = Unit

@Composable
actual fun FullScreenMode(enabled: Boolean) = Unit
