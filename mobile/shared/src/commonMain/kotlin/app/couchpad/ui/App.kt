package app.couchpad.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import app.couchpad.core.ConnectionController
import app.couchpad.core.DeviceInfo
import app.couchpad.core.FoundPc
import app.couchpad.core.LinkState
import app.couchpad.core.PairingLink
import app.couchpad.core.PcDiscovery
import app.couchpad.core.PcStore
import app.couchpad.core.RemoteSettings
import app.couchpad.core.AgentConnection
import app.couchpad.core.AgentFiles
import app.couchpad.core.Conflict
import app.couchpad.core.LocalFile
import app.couchpad.core.PhoneStorage
import app.couchpad.core.PhoneClipboard
import app.couchpad.core.PhoneFolders
import app.couchpad.core.Transfers
import app.couchpad.core.SavedPc
import app.couchpad.resources.share_no_pc
import app.couchpad.resources.share_send
import app.couchpad.resources.share_text
import app.couchpad.resources.share_title
import app.couchpad.core.WakeOnLan
import app.couchpad.resources.wake_done
import app.couchpad.resources.wake_no_network
import app.couchpad.resources.wake_timeout
import app.couchpad.resources.wake_timeout_remote
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeout
import kotlin.time.TimeMark
import kotlin.time.TimeSource
import app.couchpad.resources.Res
import app.couchpad.resources.action_cancel
import app.couchpad.resources.action_connect
import app.couchpad.resources.action_scan
import app.couchpad.resources.bad_link
import app.couchpad.resources.confirm_text
import app.couchpad.resources.confirm_title
import app.couchpad.resources.found_text
import app.couchpad.resources.found_title
import app.couchpad.resources.paste_text
import app.couchpad.resources.paste_title
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import org.jetbrains.compose.resources.getString
import org.jetbrains.compose.resources.stringResource

/**
 * Всё приложение. Платформа даёт хранилище, поиск в сети, сканер QR-кода
 * и ссылки couchpad://, открытые извне (например, из камеры телефона).
 */
@Composable
fun App(
    store: PcStore,
    remoteSettings: RemoteSettings,
    discovery: PcDiscovery,
    device: DeviceInfo,
    scanQr: (onResult: (String?) -> Unit) -> Unit,
    storage: PhoneStorage,
    clipboard: PhoneClipboard,
    incomingLinks: MutableStateFlow<String?> = remember { MutableStateFlow(null) },
    /** Файлы, присланные в приложение кнопкой «Поделиться» других приложений. */
    incomingShares: MutableStateFlow<List<LocalFile>?> = remember { MutableStateFlow(null) },
    /**
     * Соединение и передачи. На Android они живут в процессе, а не в окне: свернули приложение —
     * передача идёт дальше, а компьютер может прислать файл.
     */
    sharedController: ConnectionController? = null,
    sharedTransfers: Transfers? = null,
    /** Папки телефона, открытые компьютеру (раздел в настройках); null — платформа этого не умеет. */
    phoneFolders: PhoneFolders? = null,
) {
    CouchpadTheme {
        val scope = rememberCoroutineScope()
        val controller = remember { sharedController ?: ConnectionController(store, device, scope) }
        val transfers = remember { sharedTransfers ?: Transfers(scope, storage) }
        val clipHistory by remoteSettings.clipHistory.collectAsState()
        val shares by incomingShares.collectAsState()
        val extras = RemoteExtras(device.id, transfers, storage, clipboard, clipHistory, remoteSettings::addClip, store::setWakeHost)
        val pcs by store.pcs.collectAsState()
        val found by discovery.found.collectAsState()
        val link by controller.state.collectAsState()
        val incoming by incomingLinks.collectAsState()
        val touchpad by remoteSettings.touchpad.collectAsState()
        val snackbar = remember { SnackbarHostState() }
        var showSettings by remember { mutableStateOf(false) }

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

        // Включение по сети: какие ПК сейчас «включаются» и с какого момента.
        var waking by remember { mutableStateOf(mapOf<String, TimeMark>()) }

        fun wake(pc: SavedPc) {
            if (pc.id in waking) return
            waking = waking + (pc.id to TimeSource.Monotonic.markNow())
            scope.launch {
                val woke = try {
                    if (WakeOnLan.wake(pc) == 0) {
                        snackbar.showSnackbar(getString(Res.string.wake_no_network))
                        return@launch
                    }
                    waitUntilAwake(pc, discovery, store, device)
                } finally {
                    waking = waking - pc.id
                }
                val timeout = if (WakeOnLan.internetTarget(pc) != null) Res.string.wake_timeout_remote else Res.string.wake_timeout
                snackbar.showSnackbar(getString(if (woke) Res.string.wake_done else timeout, pc.name))
            }
        }

        LaunchedEffect(incoming) {
            incoming?.let {
                incomingLinks.value = null
                accept(it, confirm = true)
            }
        }

        if (showSettings) {
            SettingsScreen(
                settings = touchpad,
                onChange = remoteSettings::update,
                onReset = remoteSettings::reset,
                folders = phoneFolders,
                onBack = { showSettings = false },
            )
        } else when (val state = link) {
            LinkState.Idle -> HomeScreen(
                pcs = pcs,
                found = found,
                snackbar = snackbar,
                onOpen = { controller.connect(it.id) },
                waking = waking,
                onWake = ::wake,
                onFound = { foundHelp = it },
                onScan = scan,
                onPaste = { pasting = true },
                onOpenSettings = { showSettings = true },
                bottom = { TransfersList(transfers, storage) },
            )
            else -> PcScreen(
                settings = touchpad,
                onChangeSettings = remoteSettings::update,
                onOpenSettings = { showSettings = true },
                extras = extras,
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

        // «Поделиться → Couchpad»: отправить на последний компьютер в его папку для входящих.
        shares?.let { files ->
            val target = store.lastUsed()
            AlertDialog(
                onDismissRequest = { incomingShares.value = null },
                title = { Text(stringResource(Res.string.share_title)) },
                text = {
                    Text(
                        if (target == null) stringResource(Res.string.share_no_pc)
                        else stringResource(Res.string.share_text, files.size.toString(), target.name),
                    )
                },
                confirmButton = {
                    if (target != null) {
                        TextButton(onClick = {
                            incomingShares.value = null
                            val agentFiles = AgentFiles(target, device.id)
                            files.forEach { transfers.upload(agentFiles, AgentFiles.INBOX, "", it, Conflict.Rename) }
                        }) { Text(stringResource(Res.string.share_send)) }
                    }
                },
                dismissButton = { TextButton(onClick = { incomingShares.value = null }) { Text(stringResource(Res.string.action_cancel)) } },
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
                            placeholder = { Text("couchpad://pair?…") },
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

/**
 * Ждём, пока ПК загрузится: он появится в поиске по сети или ответит на подключение.
 * Пакет пробуждения повторяем в начале — вдруг первый потерялся. Не дольше двух минут.
 */
private suspend fun waitUntilAwake(pc: SavedPc, discovery: PcDiscovery, store: PcStore, device: DeviceInfo): Boolean {
    for (step in 1..40) {
        delay(3_000)
        if (step == 2 || step == 5) WakeOnLan.wake(pc)
        if (discovery.found.value.any { it.id == pc.id }) return true
        if (step % 3 == 0) {
            try {
                withTimeout(5_000) { AgentConnection.connect(store.get(pc.id) ?: pc, device).close() }
                return true
            } catch (e: CancellationException) {
                if (e !is TimeoutCancellationException) throw e
            } catch (_: Exception) {
                // Ещё загружается.
            }
        }
    }
    return false
}

