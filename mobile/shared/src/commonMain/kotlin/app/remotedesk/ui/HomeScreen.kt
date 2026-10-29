package app.remotedesk.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.IconButton
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import app.remotedesk.core.WakeOnLan
import app.remotedesk.resources.home_wake
import app.remotedesk.resources.home_waking
import app.remotedesk.resources.settings_title
import kotlinx.coroutines.delay
import kotlin.time.TimeMark
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.remotedesk.core.FoundPc
import app.remotedesk.core.SavedPc
import app.remotedesk.resources.Res
import app.remotedesk.resources.app_title
import app.remotedesk.resources.home_empty_step1
import app.remotedesk.resources.home_empty_step2
import app.remotedesk.resources.home_empty_step3
import app.remotedesk.resources.home_empty_title
import app.remotedesk.resources.home_found
import app.remotedesk.resources.home_found_hint
import app.remotedesk.resources.home_paste
import app.remotedesk.resources.home_saved
import app.remotedesk.resources.home_scan
import app.remotedesk.resources.pc_online
import app.remotedesk.resources.pc_revoked
import org.jetbrains.compose.resources.stringResource

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(
    pcs: List<SavedPc>,
    found: List<FoundPc>,
    snackbar: SnackbarHostState,
    onOpen: (SavedPc) -> Unit,
    waking: Map<String, TimeMark>,
    onWake: (SavedPc) -> Unit,
    onFound: (FoundPc) -> Unit,
    onScan: () -> Unit,
    onPaste: () -> Unit,
    onOpenSettings: () -> Unit,
    bottom: @Composable () -> Unit = {},
) {
    val onlineIds = found.map { it.id }.toSet()
    val newOnes = found.filter { f -> pcs.none { it.id == f.id } }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(Res.string.app_title)) },
                actions = {
                    IconButton(onClick = onOpenSettings) {
                        Icon(Icons.Default.Settings, contentDescription = stringResource(Res.string.settings_title))
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = { Column(Modifier.navigationBarsPadding()) { bottom() } },
        floatingActionButton = {
            ExtendedFloatingActionButton(
                onClick = onScan,
                icon = { Icon(Icons.Default.Add, contentDescription = null) },
                text = { Text(stringResource(Res.string.home_scan)) },
            )
        },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(
                start = 16.dp,
                end = 16.dp,
                top = padding.calculateTopPadding() + 8.dp,
                bottom = padding.calculateBottomPadding() + 96.dp,
            ),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (pcs.isEmpty()) {
                item { EmptyHint() }
            } else {
                item { SectionTitle(stringResource(Res.string.home_saved)) }
                items(pcs, key = { it.id }) { pc ->
                    val online = pc.id in onlineIds
                    val wakingSince = waking[pc.id]
                    PcCard(
                        name = pc.name,
                        subtitle = when {
                            pc.revoked -> stringResource(Res.string.pc_revoked)
                            wakingSince != null -> stringResource(Res.string.home_waking, elapsed(wakingSince))
                            online -> stringResource(Res.string.pc_online)
                            else -> pc.hosts.firstOrNull().orEmpty()
                        },
                        dot = when {
                            pc.revoked -> MaterialTheme.colorScheme.error
                            online -> Green
                            else -> MaterialTheme.colorScheme.outlineVariant
                        },
                        onClick = { onOpen(pc) },
                    ) {
                        when {
                            wakingSince != null -> CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                            !online && !pc.revoked && WakeOnLan.canWake(pc) ->
                                FilledTonalButton(onClick = { onWake(pc) }) { Text("⏻  " + stringResource(Res.string.home_wake)) }
                        }
                    }
                }
            }
            if (newOnes.isNotEmpty()) {
                item { SectionTitle(stringResource(Res.string.home_found)) }
                items(newOnes, key = { "found-" + it.id }) { pc ->
                    OutlinedCard(onClick = { onFound(pc) }, modifier = Modifier.fillMaxWidth()) {
                        PcRow(pc.name, stringResource(Res.string.home_found_hint), MaterialTheme.colorScheme.outline)
                    }
                }
            }
            item {
                TextButton(onClick = onPaste) { Text(stringResource(Res.string.home_paste)) }
            }
        }
    }
}

@Composable
private fun EmptyHint() {
    Column(Modifier.padding(vertical = 24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(
            stringResource(Res.string.home_empty_title),
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.SemiBold,
        )
        listOf(Res.string.home_empty_step1, Res.string.home_empty_step2, Res.string.home_empty_step3).forEach {
            Text(stringResource(it), style = MaterialTheme.typography.bodyLarge)
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = 8.dp, bottom = 2.dp),
    )
}

@Composable
private fun PcCard(name: String, subtitle: String, dot: Color, onClick: () -> Unit, trailing: @Composable () -> Unit = {}) {
    Card(onClick = onClick, modifier = Modifier.fillMaxWidth()) {
        PcRow(name, subtitle, dot, trailing)
    }
}

@Composable
private fun PcRow(name: String, subtitle: String, dot: Color, trailing: @Composable () -> Unit = {}) {
    Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(12.dp).background(dot, CircleShape))
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f)) {
            Text(name, style = MaterialTheme.typography.titleMedium)
            Spacer(Modifier.height(2.dp))
            Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        trailing()
    }
}

/** «0:23» — сколько прошло, обновляется раз в секунду. */
@Composable
private fun elapsed(since: TimeMark): String {
    var seconds by remember(since) { mutableStateOf(since.elapsedNow().inWholeSeconds) }
    LaunchedEffect(since) {
        while (true) {
            delay(1_000)
            seconds = since.elapsedNow().inWholeSeconds
        }
    }
    return "${seconds / 60}:${(seconds % 60).toString().padStart(2, '0')}"
}
