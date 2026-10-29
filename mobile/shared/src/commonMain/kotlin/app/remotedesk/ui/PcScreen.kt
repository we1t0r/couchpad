package app.remotedesk.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.saveable.rememberSaveable
import app.remotedesk.core.TouchpadSettings
import app.remotedesk.core.AgentConnection
import app.remotedesk.core.AgentFiles
import app.remotedesk.core.PhoneStorage
import app.remotedesk.core.Transfers
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.remotedesk.core.LinkState
import app.remotedesk.core.Problem
import app.remotedesk.core.SavedPc
import app.remotedesk.resources.Res
import app.remotedesk.resources.*
import org.jetbrains.compose.resources.StringResource
import org.jetbrains.compose.resources.stringResource

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PcScreen(
    state: LinkState,
    pc: SavedPc?,
    onBack: () -> Unit,
    onRetry: (pcId: String) -> Unit,
    onRescan: () -> Unit,
    onForget: (pcId: String) -> Unit,
    settings: TouchpadSettings,
    onChangeSettings: ((TouchpadSettings) -> TouchpadSettings) -> Unit,
    onOpenSettings: () -> Unit,
    extras: RemoteExtras,
) {
    val snackbar = remember { SnackbarHostState() }
    val title = pc?.name ?: when (state) {
        is LinkState.Connecting -> state.name
        is LinkState.Failed -> state.name
        is LinkState.Connected -> state.connection.agent.name
        LinkState.Idle -> ""
    }
    var menu by remember { mutableStateOf(false) }
    var forgetting by remember { mutableStateOf(false) }

    BackHandler(onBack)

    Scaffold(
        snackbarHost = { SnackbarHost(snackbar) },
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(title, maxLines = 1)
                        if (state is LinkState.Connected) {
                            Text(
                                state.pingMs?.let { stringResource(Res.string.ping, it.toString()) } ?: stringResource(Res.string.connected),
                                style = MaterialTheme.typography.labelMedium,
                                color = Green,
                            )
                        }
                    }
                },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(Res.string.action_back))
                    }
                },
                actions = {
                    IconButton(onClick = onOpenSettings) {
                        Icon(Icons.Default.Settings, contentDescription = stringResource(Res.string.settings_title))
                    }
                    if (pc != null) {
                        IconButton(onClick = { menu = true }) {
                            Icon(Icons.Default.MoreVert, contentDescription = stringResource(Res.string.action_more))
                        }
                        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                            DropdownMenuItem(
                                text = { Text(stringResource(Res.string.action_forget)) },
                                onClick = {
                                    menu = false
                                    forgetting = true
                                },
                            )
                        }
                    }
                },
            )
        },
    ) { padding ->
        if (state is LinkState.Connected) {
            RemoteControl(state, pc, settings, onChangeSettings, snackbar, extras, Modifier.fillMaxSize().padding(padding))
        } else {
            Column(
                Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                when (state) {
                    is LinkState.Connecting -> Connecting(state)
                    is LinkState.Failed -> Failed(state, canRetry = pc != null && !pc.revoked, onRetry = { pc?.let { onRetry(it.id) } }, onRescan = onRescan)
                    else -> Unit
                }
            }
        }
    }

    if (forgetting && pc != null) {
        AlertDialog(
            onDismissRequest = { forgetting = false },
            title = { Text(stringResource(Res.string.forget_title, pc.name)) },
            text = { Text(stringResource(Res.string.forget_text)) },
            confirmButton = {
                TextButton(onClick = {
                    forgetting = false
                    onForget(pc.id)
                }) { Text(stringResource(Res.string.action_forget_confirm), color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { forgetting = false }) { Text(stringResource(Res.string.action_cancel)) } },
        )
    }
}

/** Всё, что пульту нужно кроме соединения: файлы, передачи, буфер обмена. */
class RemoteExtras(
    val deviceId: String,
    val transfers: Transfers,
    val storage: PhoneStorage,
    val clipHistory: List<String>,
    val addClip: (String) -> Unit,
)

private enum class Tab(val label: StringResource, val icon: String) {
    Touchpad(Res.string.tab_touchpad, "🖱"),
    Keyboard(Res.string.tab_keyboard, "⌨"),
    Files(Res.string.tab_files, "📁"),
    Power(Res.string.tab_power, "⏻"),
    Info(Res.string.tab_info, "ℹ"),
}

/** Пульт: тачпад, клавиатура, файлы, питание и сведения о ПК — вкладками внизу. */
@Composable
private fun RemoteControl(
    state: LinkState.Connected,
    pc: SavedPc?,
    settings: TouchpadSettings,
    onChangeSettings: ((TouchpadSettings) -> TouchpadSettings) -> Unit,
    snackbar: SnackbarHostState,
    extras: RemoteExtras,
    modifier: Modifier,
) {
    var tab by rememberSaveable { mutableStateOf(Tab.Touchpad) }
    val input = state.connection.input
    KeepScreenOn(settings.keepScreenOn)
    // Ошибки агента (например, «гибернация выключена») — внизу экрана.
    LaunchedEffect(state.connection) {
        state.connection.notices.collect { snackbar.showSnackbar(it) }
    }
    // Файлы — по HTTPS на тот адрес, по которому сейчас есть связь.
    val files = remember(state.connection, pc) {
        pc?.let { AgentFiles(it.copy(hosts = (listOf(state.connection.host) + it.hosts).distinct()), extras.deviceId) }
    }
    DisposableEffect(files) { onDispose { files?.close() } }
    val notify: suspend (String) -> Unit = { snackbar.showSnackbar(it) }

    Column(modifier) {
        Box(Modifier.weight(1f).fillMaxWidth()) {
            when (tab) {
                Tab.Touchpad -> TouchpadPanel(input, settings, Modifier.fillMaxSize())
                Tab.Keyboard -> KeyboardPanel(state.connection, settings, extras.clipHistory, extras.addClip, notify, Modifier.fillMaxSize())
                Tab.Files -> if (files != null) FilesPanel(files, extras.transfers, extras.storage, notify, Modifier.fillMaxSize())
                Tab.Power -> PowerPanel(state.connection, settings, onChangeSettings, Modifier.fillMaxSize())
                Tab.Info -> Column(
                    Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) { Connected(state, pc) }
            }
        }
        NavigationBar(windowInsets = WindowInsets(0)) {
            Tab.entries.forEach { t ->
                NavigationBarItem(
                    selected = tab == t,
                    onClick = { tab = t },
                    icon = { Text(t.icon, style = MaterialTheme.typography.titleLarge) },
                    label = { Text(stringResource(t.label)) },
                )
            }
        }
    }
}

@Composable
private fun Connecting(state: LinkState.Connecting) {
    Row(Modifier.padding(vertical = 24.dp), verticalAlignment = Alignment.CenterVertically) {
        CircularProgressIndicator(Modifier.size(28.dp), strokeWidth = 3.dp)
        Spacer(Modifier.width(16.dp))
        Text(
            stringResource(if (state.pairing) Res.string.pairing else Res.string.connecting, state.name),
            style = MaterialTheme.typography.titleMedium,
        )
    }
}

@Composable
private fun Connected(state: LinkState.Connected, pc: SavedPc?) {
    val agent = state.connection.agent
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.CheckCircle, contentDescription = null, tint = Green)
                Spacer(Modifier.width(10.dp))
                Text(stringResource(Res.string.connected), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            }
            Text(
                state.pingMs?.let { stringResource(Res.string.ping, it.toString()) } ?: stringResource(Res.string.ping_waiting),
                style = MaterialTheme.typography.bodyLarge,
            )
            Text(
                stringResource(Res.string.via, "${state.connection.host}:${pc?.port ?: ""}".trimEnd(':')),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }

    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(stringResource(Res.string.info_title), style = MaterialTheme.typography.titleMedium)
            Field(Res.string.info_os, agent.os)
            Field(Res.string.info_agent, agent.version)
            if (agent.macs.isNotEmpty()) {
                val wired = stringResource(Res.string.info_wired)
                val wireless = stringResource(Res.string.info_wireless)
                Field(
                    Res.string.info_mac,
                    agent.macs.joinToString("\n") { mac -> "${mac.mac} · ${mac.name} (${if (mac.wired) wired else wireless})" },
                    monospace = true,
                )
            }
        }
    }

    WolCard(state.connection)

    Text(
        stringResource(Res.string.coming_soon),
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

/** «Включение по сети»: что проверил агент и кнопка открыть мастер на экране ПК. */
@Composable
private fun WolCard(connection: AgentConnection) {
    val status by connection.wolStatus.collectAsState()
    LaunchedEffect(connection) { connection.checkWol() }

    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(Res.string.wol_title), style = MaterialTheme.typography.titleMedium)
            val s = status
            if (s == null) {
                Text(stringResource(Res.string.wol_checking), style = MaterialTheme.typography.bodyMedium)
            } else {
                Text(
                    stringResource(if (s.ready) Res.string.wol_ready else Res.string.wol_not_ready),
                    style = MaterialTheme.typography.bodyMedium,
                )
                s.items.forEach { item ->
                    Row {
                        Text(
                            when (item.state) {
                                "ok" -> "✓"
                                "warning" -> "!"
                                "problem" -> "✗"
                                else -> "?"
                            },
                            color = when (item.state) {
                                "ok" -> Green
                                "problem", "warning" -> MaterialTheme.colorScheme.error
                                else -> MaterialTheme.colorScheme.onSurfaceVariant
                            },
                            fontWeight = FontWeight.Bold,
                            modifier = Modifier.width(22.dp),
                        )
                        Column {
                            Text(item.title, style = MaterialTheme.typography.bodyMedium)
                            item.hint?.let {
                                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                }
                Text(stringResource(Res.string.wol_how_to_test), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { connection.checkWol(openWizard = true) }) { Text(stringResource(Res.string.wol_open_wizard)) }
                TextButton(onClick = { connection.checkWol() }) { Text(stringResource(Res.string.wol_recheck)) }
            }
        }
    }
}

@Composable
private fun Field(label: StringResource, value: String, monospace: Boolean = false) {
    Column {
        Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(
            value.ifBlank { "—" },
            style = MaterialTheme.typography.bodyLarge,
            fontFamily = if (monospace) FontFamily.Monospace else null,
        )
    }
}

@Composable
private fun Failed(state: LinkState.Failed, canRetry: Boolean, onRetry: () -> Unit, onRescan: () -> Unit) {
    val (title, text) = problemText(state.problem)
    var details by remember { mutableStateOf(false) }
    val needsQr = state.problem in setOf(Problem.Revoked, Problem.WrongPc, Problem.BadCode, Problem.CodeExpired, Problem.TooManyAttempts)

    Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Default.Warning, contentDescription = null, tint = MaterialTheme.colorScheme.error)
        Spacer(Modifier.width(10.dp))
        Text(stringResource(title), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
    }
    Text(stringResource(text), style = MaterialTheme.typography.bodyLarge)

    if (canRetry && !needsQr) {
        Button(onClick = onRetry, modifier = Modifier.fillMaxWidth()) { Text(stringResource(Res.string.action_retry)) }
        OutlinedButton(onClick = onRescan, modifier = Modifier.fillMaxWidth()) { Text(stringResource(Res.string.action_rescan)) }
    } else {
        Button(onClick = onRescan, modifier = Modifier.fillMaxWidth()) { Text(stringResource(Res.string.action_rescan)) }
        if (canRetry) OutlinedButton(onClick = onRetry, modifier = Modifier.fillMaxWidth()) { Text(stringResource(Res.string.action_retry)) }
    }

    // Для опытных: что именно сказал агент или сеть.
    if (!state.detail.isNullOrBlank()) {
        TextButton(onClick = { details = !details }) { Text(stringResource(Res.string.details)) }
        if (details) {
            Text(
                "${state.problem}: ${state.detail}",
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

private fun problemText(problem: Problem): Pair<StringResource, StringResource> = when (problem) {
    Problem.Unreachable -> Res.string.problem_unreachable_title to Res.string.problem_unreachable_text
    Problem.WrongPc -> Res.string.problem_wrong_pc_title to Res.string.problem_wrong_pc_text
    Problem.BadCode -> Res.string.problem_bad_code_title to Res.string.problem_bad_code_text
    Problem.CodeExpired -> Res.string.problem_code_expired_title to Res.string.problem_code_expired_text
    Problem.TooManyAttempts -> Res.string.problem_too_many_title to Res.string.problem_too_many_text
    Problem.Revoked -> Res.string.problem_revoked_title to Res.string.problem_revoked_text
    Problem.Timeout -> Res.string.problem_timeout_title to Res.string.problem_timeout_text
    Problem.Incompatible -> Res.string.problem_incompatible_title to Res.string.problem_incompatible_text
    Problem.Protocol -> Res.string.problem_protocol_title to Res.string.problem_protocol_text
    Problem.Closed -> Res.string.problem_closed_title to Res.string.problem_closed_text
}
