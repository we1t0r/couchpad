package app.couchpad.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.AcUnit
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.DesktopAccessDisabled
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.RestartAlt
import androidx.compose.material.icons.filled.Timer
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.couchpad.core.AgentConnection
import app.couchpad.core.PowerAction
import app.couchpad.core.PowerTimer
import app.couchpad.core.TouchpadSettings
import app.couchpad.resources.*
import kotlinx.coroutines.delay
import org.jetbrains.compose.resources.StringResource
import org.jetbrains.compose.resources.stringResource

/** Минуты для таймера. */
private val delays = listOf(5, 15, 30, 60, 90, 120)

private val PowerAction.label: StringResource
    get() = when (this) {
        PowerAction.Shutdown -> Res.string.pw_shutdown
        PowerAction.Restart -> Res.string.pw_restart
        PowerAction.Sleep -> Res.string.pw_sleep
        PowerAction.Hibernate -> Res.string.pw_hibernate
        PowerAction.Lock -> Res.string.pw_lock
        PowerAction.SignOut -> Res.string.pw_signout
        PowerAction.MonitorsOff -> Res.string.pw_monitors_off
        PowerAction.MonitorsOn -> Res.string.pw_monitors_on
    }

private val PowerAction.icon: ImageVector
    get() = when (this) {
        PowerAction.Shutdown -> Icons.Default.PowerSettingsNew
        PowerAction.Restart -> Icons.Default.RestartAlt
        PowerAction.Sleep -> Icons.Default.Bedtime
        PowerAction.Hibernate -> Icons.Default.AcUnit
        PowerAction.Lock -> Icons.Default.Lock
        PowerAction.SignOut -> Icons.AutoMirrored.Filled.Logout
        PowerAction.MonitorsOff -> Icons.Default.DesktopAccessDisabled
        PowerAction.MonitorsOn -> Icons.Default.DesktopWindows
    }

/** Что спросить в диалоге: конкретное действие или «Таймер…» с выбором действия. */
private class PowerRequest(val action: PowerAction?, val timerOnly: Boolean)

@Composable
fun PowerPanel(
    connection: AgentConnection,
    settings: TouchpadSettings,
    onChangeSettings: ((TouchpadSettings) -> TouchpadSettings) -> Unit,
    modifier: Modifier = Modifier,
) {
    val power = connection.power
    val timer by connection.powerTimer.collectAsState()
    var request by remember { mutableStateOf<PowerRequest?>(null) }

    fun tap(action: PowerAction) {
        when {
            action == PowerAction.Lock -> power.run(action)
            settings.confirmPower -> request = PowerRequest(action, timerOnly = false)
            else -> power.run(action)
        }
    }

    Column(
        modifier.verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        timer?.let { TimerCard(it, onCancel = power::cancel) }

        // Мониторы — то, чем пользуются чаще всего: компьютер при этом работает дальше.
        SectionTitle(stringResource(Res.string.pw_monitors))
        Panel {
            Button(
                onClick = { power.run(PowerAction.MonitorsOff, keepMonitorsOff = settings.monitorsWakeByAppOnly) },
                modifier = Modifier.fillMaxWidth(),
                shape = MaterialTheme.shapes.large,
                contentPadding = PaddingValues(vertical = 18.dp),
            ) {
                Icon(Icons.Default.DesktopAccessDisabled, contentDescription = null)
                Spacer(Modifier.width(10.dp))
                Text(stringResource(Res.string.pw_monitors_off), style = MaterialTheme.typography.titleMedium)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { power.run(PowerAction.MonitorsOn) }, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Default.DesktopWindows, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(Res.string.pw_turn_on))
                }
                OutlinedButton(onClick = { request = PowerRequest(PowerAction.MonitorsOff, timerOnly = true) }, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Default.Timer, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(Res.string.pw_later))
                }
            }
            Hint(stringResource(if (settings.monitorsWakeByAppOnly) Res.string.pw_monitors_hint_app else Res.string.pw_monitors_hint_any))
            Row(
                Modifier.fillMaxWidth().clickable { onChangeSettings { it.copy(monitorsWakeByAppOnly = !it.monitorsWakeByAppOnly) } },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(Res.string.s_wake_title), style = MaterialTheme.typography.bodyLarge)
                    Hint(stringResource(Res.string.s_wake_sub))
                }
                Spacer(Modifier.width(12.dp))
                Switch(
                    checked = settings.monitorsWakeByAppOnly,
                    onCheckedChange = { v -> onChangeSettings { it.copy(monitorsWakeByAppOnly = v) } },
                )
            }
        }

        SectionTitle(stringResource(Res.string.pw_computer))
        val actions = listOf(
            PowerAction.Lock, PowerAction.Sleep, PowerAction.Hibernate,
            PowerAction.SignOut, PowerAction.Restart, PowerAction.Shutdown,
        )
        actions.chunked(3).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.forEach { action ->
                    PowerTile(action.icon, stringResource(action.label), danger = action == PowerAction.Shutdown, modifier = Modifier.weight(1f)) {
                        tap(action)
                    }
                }
            }
        }
        OutlinedButton(onClick = { request = PowerRequest(null, timerOnly = true) }, modifier = Modifier.fillMaxWidth(), contentPadding = PaddingValues(vertical = 12.dp)) {
            Icon(Icons.Default.Timer, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(stringResource(Res.string.pw_timer))
        }
        Hint(stringResource(Res.string.pw_note), Modifier.padding(horizontal = 4.dp))
    }

    request?.let { r ->
        PowerDialog(
            request = r,
            onDismiss = { request = null },
            onConfirm = { action, minutes, force ->
                request = null
                val keep = action == PowerAction.MonitorsOff && settings.monitorsWakeByAppOnly
                if (minutes == 0) power.run(action, force, keep) else power.schedule(action, minutes * 60, force, keep)
            },
        )
    }
}

/** Плитка действия: значок сверху, подпись снизу. «Выключить» — красным, чтобы не нажать случайно. */
@Composable
private fun PowerTile(icon: ImageVector, label: String, danger: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val colors = MaterialTheme.colorScheme
    Surface(
        onClick = onClick,
        modifier = modifier.height(96.dp),
        shape = MaterialTheme.shapes.large,
        color = if (danger) colors.errorContainer else colors.surfaceContainer,
        contentColor = if (danger) colors.onErrorContainer else colors.onSurface,
    ) {
        Column(
            Modifier.padding(8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Icon(icon, contentDescription = null, tint = if (danger) colors.onErrorContainer else colors.primary, modifier = Modifier.size(28.dp))
            Spacer(Modifier.height(8.dp))
            Text(label, style = MaterialTheme.typography.labelLarge, textAlign = TextAlign.Center, maxLines = 2)
        }
    }
}

@Composable
private fun TimerCard(timer: PowerTimer, onCancel: () -> Unit) {
    var left by remember(timer) { mutableLongStateOf(timer.remainingMs) }
    LaunchedEffect(timer) {
        while (true) {
            left = timer.remainingMs
            delay(500)
        }
    }
    val seconds = (left + 999) / 1000
    val time = if (seconds >= 3600) "${seconds / 3600}:${(seconds % 3600 / 60).toString().padStart(2, '0')}:${(seconds % 60).toString().padStart(2, '0')}"
    else "${seconds / 60}:${(seconds % 60).toString().padStart(2, '0')}"
    Card(
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.tertiaryContainer),
        shape = MaterialTheme.shapes.large,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.Timer, contentDescription = null, modifier = Modifier.size(28.dp))
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(stringResource(timer.action.label), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(stringResource(Res.string.pw_in, time), style = MaterialTheme.typography.bodyLarge)
            }
            TextButton(onClick = onCancel) { Text(stringResource(Res.string.action_cancel)) }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PowerDialog(
    request: PowerRequest,
    onDismiss: () -> Unit,
    onConfirm: (action: PowerAction, minutes: Int, force: Boolean) -> Unit,
) {
    var action by remember { mutableStateOf(request.action ?: PowerAction.Shutdown) }
    // 0 — сейчас.
    var minutes by remember { mutableStateOf(if (request.timerOnly) 30 else 0) }
    var force by remember { mutableStateOf(false) }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(if (request.action == null) Icons.Default.Timer else action.icon, contentDescription = null) },
        title = { Text(stringResource(if (request.action == null) Res.string.pw_timer else action.label) + "?") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                if (request.action == null) {
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf(PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Hibernate, PowerAction.MonitorsOff)
                            .forEach { a -> FilterChip(selected = action == a, onClick = { action = a }, label = { Text(stringResource(a.label)) }) }
                    }
                }
                Text(stringResource(Res.string.pw_when), style = MaterialTheme.typography.labelLarge)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (!request.timerOnly) {
                        FilterChip(selected = minutes == 0, onClick = { minutes = 0 }, label = { Text(stringResource(Res.string.pw_now)) })
                    }
                    delays.forEach { m ->
                        FilterChip(selected = minutes == m, onClick = { minutes = m }, label = { Text(stringResource(Res.string.pw_minutes, m.toString())) })
                    }
                }
                if (action.canForce) {
                    Row(Modifier.clickable { force = !force }, verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(checked = force, onCheckedChange = { force = it })
                        Text(stringResource(Res.string.pw_force))
                    }
                    Hint(stringResource(if (force) Res.string.pw_force_on else Res.string.pw_force_off))
                }
                if (minutes > 0 && action != PowerAction.MonitorsOff) {
                    Hint(stringResource(Res.string.pw_timer_note))
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(action, minutes, force && action.canForce) }) {
                Text(stringResource(if (minutes == 0) action.label else Res.string.pw_set_timer))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(Res.string.action_cancel)) } },
    )
}
