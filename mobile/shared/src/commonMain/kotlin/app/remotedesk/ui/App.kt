package app.remotedesk.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import app.remotedesk.core.ConnectionController
import app.remotedesk.core.DeviceInfo
import app.remotedesk.core.FoundPc
import app.remotedesk.core.LinkState
import app.remotedesk.core.PairingLink
import app.remotedesk.core.PcDiscovery
import app.remotedesk.core.PcStore
import app.remotedesk.resources.Res
import app.remotedesk.resources.action_cancel
import app.remotedesk.resources.action_connect
import app.remotedesk.resources.action_scan
import app.remotedesk.resources.bad_link
import app.remotedesk.resources.confirm_text
import app.remotedesk.resources.confirm_title
import app.remotedesk.resources.found_text
import app.remotedesk.resources.found_title
import app.remotedesk.resources.paste_text
import app.remotedesk.resources.paste_title
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import org.jetbrains.compose.resources.getString
import org.jetbrains.compose.resources.stringResource

/**
 * Всё приложение. Платформа даёт хранилище, поиск в сети, сканер QR-кода
 * и ссылки rdesk://, открытые извне (например, из камеры телефона).
 */
@Composable
fun App(
    store: PcStore,
    discovery: PcDiscovery,
    device: DeviceInfo,
    scanQr: (onResult: (String?) -> Unit) -> Unit,
    incomingLinks: MutableStateFlow<String?> = remember { MutableStateFlow(null) },
) {
    RemoteDeskTheme {
        val scope = rememberCoroutineScope()
        val controller = remember { ConnectionController(store, device, scope) }
        val pcs by store.pcs.collectAsState()
        val found by discovery.found.collectAsState()
        val link by controller.state.collectAsState()
        val incoming by incomingLinks.collectAsState()
        val snackbar = remember { SnackbarHostState() }

        var confirmLink by remember { mutableStateOf<PairingLink?>(null) }
        var foundHelp by remember { mutableStateOf<FoundPc?>(null) }
        var pasting by remember { mutableStateOf(false) }

        // Компьютер сменил IP — запоминаем новый адрес (только если ключ тот же).
        LaunchedEffect(found) {
            found.forEach { store.updateAddress(it.id, it.fingerprint, it.hosts, it.port) }
        }

        fun accept(text: String?, confirm: Boolean) {
            if (text == null) return
            val parsed = PairingLink.parse(text)
            when {
                parsed == null -> scope.launch { snackbar.showSnackbar(getString(Res.string.bad_link)) }
                confirm -> confirmLink = parsed
                else -> controller.pair(parsed)
            }
        }

        val scan = { scanQr { accept(it, confirm = false) } }

        LaunchedEffect(incoming) {
            incoming?.let {
                incomingLinks.value = null
                accept(it, confirm = true)
            }
        }

        when (val state = link) {
            LinkState.Idle -> HomeScreen(
                pcs = pcs,
                found = found,
                snackbar = snackbar,
                onOpen = { controller.connect(it.id) },
                onFound = { foundHelp = it },
                onScan = scan,
                onPaste = { pasting = true },
            )
            else -> PcScreen(
                state = state,
                pc = when (state) {
                    is LinkState.Connecting -> state.pcId
                    is LinkState.Connected -> state.pcId
                    is LinkState.Failed -> state.pcId
                    LinkState.Idle -> null
                }?.let { id -> pcs.firstOrNull { it.id == id } },
                onBack = controller::disconnect,
                onRetry = { id -> controller.connect(id) },
                onRescan = {
                    controller.disconnect()
                    scan()
                },
                onForget = { id ->
                    controller.disconnect()
                    store.remove(id)
                },
            )
        }

        confirmLink?.let { pending ->
            AlertDialog(
                onDismissRequest = { confirmLink = null },
                title = { Text(stringResource(Res.string.confirm_title)) },
                text = { Text(stringResource(Res.string.confirm_text, pending.name, pending.hosts.first())) },
                confirmButton = {
                    TextButton(onClick = {
                        confirmLink = null
                        controller.pair(pending)
                    }) { Text(stringResource(Res.string.action_connect)) }
                },
                dismissButton = { TextButton(onClick = { confirmLink = null }) { Text(stringResource(Res.string.action_cancel)) } },
            )
        }

        foundHelp?.let { pc ->
            AlertDialog(
                onDismissRequest = { foundHelp = null },
                title = { Text(stringResource(Res.string.found_title, pc.name)) },
                text = { Text(stringResource(Res.string.found_text)) },
                confirmButton = {
                    TextButton(onClick = {
                        foundHelp = null
                        scan()
                    }) { Text(stringResource(Res.string.action_scan)) }
                },
                dismissButton = { TextButton(onClick = { foundHelp = null }) { Text(stringResource(Res.string.action_cancel)) } },
            )
        }

        if (pasting) {
            var text by remember { mutableStateOf("") }
            AlertDialog(
                onDismissRequest = { pasting = false },
                title = { Text(stringResource(Res.string.paste_title)) },
                text = {
                    Column {
                        Text(stringResource(Res.string.paste_text), style = MaterialTheme.typography.bodyMedium)
                        OutlinedTextField(
                            value = text,
                            onValueChange = { text = it },
                            placeholder = { Text("rdesk://pair?…") },
                            singleLine = true,
                            modifier = Modifier.padding(top = 12.dp),
                        )
                    }
                },
                confirmButton = {
                    TextButton(onClick = {
                        pasting = false
                        accept(text, confirm = false)
                    }, enabled = text.isNotBlank()) { Text(stringResource(Res.string.action_connect)) }
                },
                dismissButton = { TextButton(onClick = { pasting = false }) { Text(stringResource(Res.string.action_cancel)) } },
            )
        }
    }
}

val Blue = Color(0xFF2563EB)
val Green = Color(0xFF1D9E75)

@Composable
fun RemoteDeskTheme(content: @Composable () -> Unit) {
    val colors = if (isSystemInDarkTheme()) {
        darkColorScheme(primary = Color(0xFF8AB4FF), onPrimary = Color(0xFF0B1F44), secondary = Color(0xFF6FD3B0))
    } else {
        lightColorScheme(primary = Blue, onPrimary = Color.White, secondary = Green)
    }
    MaterialTheme(colorScheme = colors, content = content)
}
