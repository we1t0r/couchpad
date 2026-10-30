package app.remotedesk.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.union
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.filled.Build
import androidx.compose.material.icons.filled.Cable
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Error
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Public
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.automirrored.filled.ScreenShare
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.graphics.vector.ImageVector
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
import app.remotedesk.core.PhoneClipboard
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
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Surface
import androidx.compose.material3.Scaffold
import androidx.compose.material3.ScaffoldDefaults
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
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.remotedesk.core.LinkState
import app.remotedesk.core.Problem
import app.remotedesk.core.SavedPc
import app.remotedesk.core.WakeOnLan
import app.remotedesk.core.internetHost
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.OutlinedTextField
import androidx.compose.ui.text.input.KeyboardType
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
    /** Номер вкладки пульта при открытии (для снимков экрана в тестах). */
    initialTab: Int = 0,
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
    // «Во весь экран» на вкладке «Экран»: без шапки и вкладок.
    var immersive by rememberSaveable { mutableStateOf(false) }
    if (immersive) BackHandler { immersive = false }

    Scaffold(
        // Клавиатура телефона поднимает содержимое, чтобы поле ввода не пряталось под ней.
        contentWindowInsets = ScaffoldDefaults.contentWindowInsets.union(WindowInsets.ime),
        snackbarHost = { SnackbarHost(snackbar) },
        topBar = {
            if (!immersive) TopAppBar(
                title = {
                    Column {
                        Text(title, maxLines = 1, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                        if (state is LinkState.Connected) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Box(Modifier.size(8.dp).background(Green, CircleShape))
                                Spacer(Modifier.width(6.dp))
                                Text(
                                    (if (state.connection.viaInternet) stringResource(Res.string.via_internet) + " · " else "") +
                                    state.pingMs?.let { stringResource(Res.string.ping, it.toString()) } ?: stringResource(Res.string.connected),
                                    style = MaterialTheme.typography.labelMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
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
            RemoteControl(state, pc, settings, onChangeSettings, snackbar, extras, initialTab, immersive, { immersive = it }, Modifier.fillMaxSize().padding(padding))
        } else {
            Column(
                Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                when (state) {
                    is LinkState.Connecting -> Connecting(state)
                    is LinkState.Failed -> Failed(state, canRetry = pc != null && !pc.revoked, onRetry = { pc?.let { onRetry(it.id) } }, onRescan = onRescan, homeAddress = pc?.internetHost)
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
    val clipboard: PhoneClipboard,
    val clipHistory: List<String>,
    val addClip: (String) -> Unit,
    val setWakeHost: (pcId: String, host: String) -> Unit,
)

private enum class Tab(val label: StringResource, val icon: ImageVector) {
    Touchpad(Res.string.tab_touchpad, Icons.Default.Mouse),
    Screen(Res.string.tab_screen, Icons.AutoMirrored.Filled.ScreenShare),
    Keyboard(Res.string.tab_keyboard, Icons.Default.Keyboard),
    Files(Res.string.tab_files, Icons.Default.Folder),
    Power(Res.string.tab_power, Icons.Default.PowerSettingsNew),
    Info(Res.string.tab_info, Icons.Default.Info),
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
    initialTab: Int,
    immersive: Boolean,
    onImmersive: (Boolean) -> Unit,
    modifier: Modifier,
) {
    // На экране входа Windows файлов и буфера обмена нет, а нужен в первую очередь экран — ввести PIN.
    val signIn = state.connection.signIn
    val tabs = if (signIn) Tab.entries - Tab.Files else Tab.entries
    var tab by rememberSaveable(signIn) { mutableStateOf(if (signIn) Tab.Screen else Tab.entries[initialTab]) }
    // Ушли с вкладки «Экран» — возвращаем шапку и вкладки.
    LaunchedEffect(tab) { if (tab != Tab.Screen) onImmersive(false) }
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
        if (signIn) SignInBanner()
        Box(Modifier.weight(1f).fillMaxWidth()) {
            when (tab) {
                Tab.Touchpad -> TouchpadPanel(input, settings, Modifier.fillMaxSize())
                Tab.Screen -> ScreenPanel(state.connection, settings, onChangeSettings, immersive, onImmersive, Modifier.fillMaxSize())
                Tab.Keyboard -> KeyboardPanel(state.connection, settings, extras.clipboard, files.takeIf { !signIn }, extras.clipHistory, extras.addClip, notify, Modifier.fillMaxSize(), showClipboard = !signIn)
                Tab.Files -> if (files != null) FilesPanel(files, extras.transfers, extras.storage, notify, Modifier.fillMaxSize())
                Tab.Power -> PowerPanel(state.connection, settings, onChangeSettings, Modifier.fillMaxSize())
                Tab.Info -> Column(
                    Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) { Connected(state, pc, extras.setWakeHost) }
            }
        }
        // Пока открыта клавиатура телефона, вкладки прячутся — полю ввода остаётся больше места.
        if (!immersive && WindowInsets.ime.getBottom(LocalDensity.current) == 0) NavigationBar(windowInsets = WindowInsets(0), containerColor = MaterialTheme.colorScheme.surfaceContainer) {
            tabs.forEach { t ->
                NavigationBarItem(
                    selected = tab == t,
                    onClick = { tab = t },
                    icon = { Icon(t.icon, contentDescription = null) },
                    label = { Text(stringResource(t.label), maxLines = 1, softWrap = false) },
                )
            }
        }
    }
}

@Composable
private fun Connecting(state: LinkState.Connecting) {
    Column(
        Modifier.fillMaxWidth().padding(top = 96.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(20.dp),
    ) {
        CircularProgressIndicator(Modifier.size(48.dp), strokeWidth = 4.dp)
        Text(
            stringResource(if (state.pairing) Res.string.pairing else Res.string.connecting, state.name),
            style = MaterialTheme.typography.titleMedium,
        )
    }
}

@Composable
private fun Connected(state: LinkState.Connected, pc: SavedPc?, setWakeHost: (String, String) -> Unit) {
    val agent = state.connection.agent
    Panel {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconBadge(Icons.Default.Computer)
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(agent.name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Default.CheckCircle, contentDescription = null, tint = Green, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(
                        stringResource(Res.string.connected) + " · " +
                            (if (state.connection.viaInternet) stringResource(Res.string.via_internet) + " · " else "") +
                            (state.pingMs?.let { stringResource(Res.string.ping, it.toString()) } ?: stringResource(Res.string.ping_waiting)),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }

    SectionTitle(stringResource(Res.string.info_title))
    Panel {
        Field(stringResource(Res.string.info_os), agent.os)
        Field(stringResource(Res.string.info_agent), agent.version)
        Field(stringResource(Res.string.via, "").trimEnd(' ', ':'), "${state.connection.host}:${pc?.port ?: ""}".trimEnd(':'))
        if (agent.macs.isNotEmpty()) {
            val wired = stringResource(Res.string.info_wired)
            val wireless = stringResource(Res.string.info_wireless)
            Column {
                Text(stringResource(Res.string.info_mac), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                agent.macs.forEach { mac ->
                    Row(Modifier.padding(top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            if (mac.wired) Icons.Default.Cable else Icons.Default.Wifi,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(18.dp),
                        )
                        Spacer(Modifier.width(8.dp))
                        Column {
                            Text("${mac.name} (${if (mac.wired) wired else wireless})", style = MaterialTheme.typography.bodyLarge)
                            Text(mac.mac, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }

    if (pc != null) RemoteWakeCard(pc, setWakeHost)

    SectionTitle(stringResource(Res.string.wol_title))
    WolCard(state.connection)
}

/** «Включение по сети»: что проверил агент и кнопка открыть мастер на экране ПК. */
@Composable
private fun WolCard(connection: AgentConnection) {
    val status by connection.wolStatus.collectAsState()
    LaunchedEffect(connection) { connection.checkWol() }

    Panel {
        val s = status
        if (s == null) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(12.dp))
                Text(stringResource(Res.string.wol_checking), style = MaterialTheme.typography.bodyMedium)
            }
        } else {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (s.ready) IconBadge(Icons.Default.PowerSettingsNew, Green, Green.copy(alpha = 0.16f), 40.dp)
                else IconBadge(Icons.Default.Build, MaterialTheme.colorScheme.onTertiaryContainer, MaterialTheme.colorScheme.tertiaryContainer, 40.dp)
                Spacer(Modifier.width(12.dp))
                Text(
                    stringResource(if (s.ready) Res.string.wol_ready else Res.string.wol_not_ready),
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.weight(1f),
                )
            }
            s.items.forEach { item ->
                Row {
                    val (icon, tint) = when (item.state) {
                        "ok" -> Icons.Default.CheckCircle to Green
                        "warning" -> Icons.Default.Warning to MaterialTheme.colorScheme.tertiary
                        "problem" -> Icons.Default.Error to MaterialTheme.colorScheme.error
                        else -> Icons.AutoMirrored.Filled.HelpOutline to MaterialTheme.colorScheme.onSurfaceVariant
                    }
                    Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(20.dp))
                    Spacer(Modifier.width(10.dp))
                    Column {
                        Text(item.title, style = MaterialTheme.typography.bodyMedium)
                        item.hint?.let { Hint(it) }
                    }
                }
            }
            Hint(stringResource(Res.string.wol_how_to_test))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedButton(onClick = { connection.checkWol(openWizard = true) }) { Text(stringResource(Res.string.wol_open_wizard)) }
            TextButton(onClick = { connection.checkWol() }) { Text(stringResource(Res.string.wol_recheck)) }
        }
    }
}

/** «Не из дома»: адрес дома в интернете — для подключения через проброс порта и для кнопки «Включить». */
@Composable
private fun RemoteWakeCard(pc: SavedPc, setWakeHost: (String, String) -> Unit) {
    var text by remember(pc.id) { mutableStateOf(pc.wakeHost) }
    val valid = text.isBlank() || WakeOnLan.internetTarget(pc.copy(wakeHost = text)) != null
    val saved = text.trim() == pc.wakeHost

    Panel {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconBadge(Icons.Default.Public, size = 40.dp)
            Spacer(Modifier.width(12.dp))
            Text(stringResource(Res.string.wol_remote_title), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        }
        Hint(stringResource(Res.string.wol_remote_text, pc.port.toString()))
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text(stringResource(Res.string.wol_remote_hint)) },
            singleLine = true,
            isError = !valid,
            supportingText = if (valid) null else ({ Text(stringResource(Res.string.wol_remote_bad)) }),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            shape = MaterialTheme.shapes.medium,
            modifier = Modifier.fillMaxWidth(),
        )
        Button(onClick = { setWakeHost(pc.id, text) }, enabled = valid && !saved) {
            if (saved && text.isNotBlank()) {
                Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
            }
            Text(stringResource(if (saved && text.isNotBlank()) Res.string.wol_remote_saved else Res.string.wol_remote_save))
        }
    }
}

@Composable
private fun Field(label: String, value: String) {
    Column {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value.ifBlank { "—" }, style = MaterialTheme.typography.bodyLarge)
    }
}

@Composable
private fun Failed(state: LinkState.Failed, canRetry: Boolean, onRetry: () -> Unit, onRescan: () -> Unit, homeAddress: String?) {
    val (title, text) = problemText(state.problem)
    var details by remember { mutableStateOf(false) }
    val needsQr = state.problem in setOf(Problem.Revoked, Problem.WrongPc, Problem.BadCode, Problem.CodeExpired, Problem.TooManyAttempts)

    Spacer(Modifier.height(24.dp))
    IconBadge(Icons.Default.WifiOff, MaterialTheme.colorScheme.onErrorContainer, MaterialTheme.colorScheme.errorContainer, 72.dp)
    Text(stringResource(title), style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
    Text(stringResource(text), style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
    // Не дома: подсказать, как подключаться через интернет, или почему адрес дома мог перестать работать.
    if (state.problem == Problem.Unreachable || state.problem == Problem.Timeout) {
        Hint(if (homeAddress == null) stringResource(Res.string.problem_away_no_address) else stringResource(Res.string.problem_away_address, homeAddress))
    }
    Spacer(Modifier.height(4.dp))

    val retry: @Composable () -> Unit = {
        Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(8.dp))
        Text(stringResource(Res.string.action_retry))
    }
    val rescan: @Composable () -> Unit = {
        Icon(Icons.Default.QrCodeScanner, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(8.dp))
        Text(stringResource(Res.string.action_rescan))
    }
    val big = PaddingValues(vertical = 14.dp)
    if (canRetry && !needsQr) {
        Button(onClick = onRetry, modifier = Modifier.fillMaxWidth(), contentPadding = big) { retry() }
        OutlinedButton(onClick = onRescan, modifier = Modifier.fillMaxWidth(), contentPadding = big) { rescan() }
    } else {
        Button(onClick = onRescan, modifier = Modifier.fillMaxWidth(), contentPadding = big) { rescan() }
        if (canRetry) OutlinedButton(onClick = onRetry, modifier = Modifier.fillMaxWidth(), contentPadding = big) { retry() }
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

/** ПК на экране входа или блокировки Windows: подсказать, что сейчас можно. */
@Composable
private fun SignInBanner() {
    Surface(color = MaterialTheme.colorScheme.secondaryContainer, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.Lock, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer, modifier = Modifier.size(20.dp))
            Spacer(Modifier.width(12.dp))
            Text(stringResource(Res.string.signin_banner), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSecondaryContainer)
        }
    }
}
