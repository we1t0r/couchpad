package app.couchpad.ui

import androidx.compose.runtime.Composable

@Composable
actual fun BackHandler(onBack: () -> Unit) = Unit

@Composable
actual fun KeepScreenOn(enabled: Boolean) = Unit

@Composable
actual fun FullScreenMode(enabled: Boolean) = Unit

@Composable
internal actual fun VideoSurface(connection: app.couchpad.core.AgentConnection, modifier: androidx.compose.ui.Modifier) = Unit
