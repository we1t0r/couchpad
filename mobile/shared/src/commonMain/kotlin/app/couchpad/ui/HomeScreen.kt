package app.couchpad.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.couchpad.core.FoundPc
import app.couchpad.core.SavedPc
import app.couchpad.core.WakeOnLan
import app.couchpad.resources.Res
import app.couchpad.resources.app_title
import app.couchpad.resources.home_empty_step1
import app.couchpad.resources.home_empty_step2
import app.couchpad.resources.home_empty_step3
import app.couchpad.resources.home_empty_title
import app.couchpad.resources.home_found
import app.couchpad.resources.home_found_hint
import app.couchpad.resources.home_paste
import app.couchpad.resources.home_saved
import app.couchpad.resources.home_scan
import app.couchpad.resources.home_wake
import app.couchpad.resources.home_waking
import app.couchpad.resources.pc_online
import app.couchpad.resources.pc_revoked
import app.couchpad.resources.settings_title
import kotlinx.coroutines.delay
import kotlin.time.TimeMark
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
                title = { Text(stringResource(Res.string.app_title), fontWeight = FontWeight.Bold) },
                actions = {
                    IconButton(onClick = onOpenSettings) {
                        Icon(Icons.Default.Settings, contentDescription = stringResource(Res.string.settings_title))
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = { Column(Modifier.navigationBarsPadding()) { bottom() } },
        floatingActionButton = {
            // На пустом экране главная кнопка — внутри подсказки, второй не нужно.
            if (pcs.isNotEmpty()) {
                ExtendedFloatingActionButton(
                    onClick = onScan,
                    icon = { Icon(Icons.Default.QrCodeScanner, contentDescription = null) },
                    text = { Text(stringResource(Res.string.home_scan)) },
                    containerColor = MaterialTheme.colorScheme.primary,
                    contentColor = MaterialTheme.colorScheme.onPrimary,
                )
            }
        },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(
                start = 16.dp,
                end = 16.dp,
                top = padding.calculateTopPadding() + 4.dp,
                bottom = padding.calculateBottomPadding() + 96.dp,
            ),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (pcs.isEmpty()) {
                item { EmptyHint(onScan, onPaste) }
            } else {
                item { SectionTitle(stringResource(Res.string.home_saved)) }
                items(pcs, key = { it.id }) { pc ->
                    val online = pc.id in onlineIds
                    val wakingSince = waking[pc.id]
                    val status = when {
                        pc.revoked -> MaterialTheme.colorScheme.error
                        online -> Green
                        else -> MaterialTheme.colorScheme.outline
                    }
                    PcCard(
                        name = pc.name,
                        subtitle = when {
                            pc.revoked -> stringResource(Res.string.pc_revoked)
                            wakingSince != null -> stringResource(Res.string.home_waking, elapsed(wakingSince))
                            online -> stringResource(Res.string.pc_online)
                            else -> pc.hosts.firstOrNull().orEmpty()
                        },
                        status = status,
                        active = online,
                        onClick = { onOpen(pc) },
                    ) {
                        when {
                            wakingSince != null -> CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                            !online && !pc.revoked && WakeOnLan.canWake(pc) ->
                                FilledTonalButton(onClick = { onWake(pc) }, contentPadding = PaddingValues(horizontal = 14.dp)) {
                                    Icon(Icons.Default.PowerSettingsNew, contentDescription = null, modifier = Modifier.size(18.dp))
                                    Spacer(Modifier.width(6.dp))
                                    Text(stringResource(Res.string.home_wake))
                                }
                            else -> Icon(
                                Icons.AutoMirrored.Filled.KeyboardArrowRight,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }
            if (newOnes.isNotEmpty()) {
                item { SectionTitle(stringResource(Res.string.home_found)) }
                items(newOnes, key = { "found-" + it.id }) { pc ->
                    Panel(onClick = { onFound(pc) }) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            IconBadge(Icons.Default.Wifi, MaterialTheme.colorScheme.onSurfaceVariant, MaterialTheme.colorScheme.surfaceContainerHigh)
                            Spacer(Modifier.width(14.dp))
                            Column(Modifier.weight(1f)) {
                                Text(pc.name, style = MaterialTheme.typography.titleMedium)
                                Hint(stringResource(Res.string.home_found_hint))
                            }
                        }
                    }
                }
            }
            if (pcs.isNotEmpty()) {
                item {
                    TextButton(onClick = onPaste) {
                        Icon(Icons.Default.Link, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(8.dp))
                        Text(stringResource(Res.string.home_paste))
                    }
                }
            }
        }
    }
}

/** Первый запуск: три шага с номерами и большая кнопка «Сканировать». */
@Composable
private fun EmptyHint(onScan: () -> Unit, onPaste: () -> Unit) {
    Column(Modifier.padding(top = 16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        IconBadge(Icons.Default.Computer, size = 72.dp)
        Text(
            stringResource(Res.string.home_empty_title),
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.SemiBold,
        )
        Panel {
            listOf(Res.string.home_empty_step1, Res.string.home_empty_step2, Res.string.home_empty_step3).forEachIndexed { i, step ->
                Row {
                    Box(
                        Modifier.size(28.dp).background(MaterialTheme.colorScheme.primaryContainer, CircleShape),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            "${i + 1}",
                            style = MaterialTheme.typography.labelLarge,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.onPrimaryContainer,
                        )
                    }
                    Spacer(Modifier.width(14.dp))
                    Text(stringResource(step), style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f).padding(top = 3.dp))
                }
            }
        }
        Button(onClick = onScan, modifier = Modifier.fillMaxWidth().padding(top = 4.dp), contentPadding = PaddingValues(vertical = 16.dp)) {
            Icon(Icons.Default.QrCodeScanner, contentDescription = null)
            Spacer(Modifier.width(10.dp))
            Text(stringResource(Res.string.home_scan), style = MaterialTheme.typography.titleMedium)
        }
        TextButton(onClick = onPaste, modifier = Modifier.align(Alignment.CenterHorizontally)) {
            Text(stringResource(Res.string.home_paste))
        }
    }
}

@Composable
private fun PcCard(
    name: String,
    subtitle: String,
    status: Color,
    active: Boolean,
    onClick: () -> Unit,
    trailing: @Composable () -> Unit,
) {
    Panel(onClick = onClick) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box {
                if (active) IconBadge(Icons.Default.Computer)
                else IconBadge(Icons.Default.Computer, MaterialTheme.colorScheme.onSurfaceVariant, MaterialTheme.colorScheme.surfaceContainerHigh)
                // Точка состояния в углу значка.
                Box(
                    Modifier.align(Alignment.BottomEnd).size(14.dp)
                        .background(MaterialTheme.colorScheme.surfaceContainer, CircleShape)
                        .padding(2.5.dp)
                        .background(status, CircleShape),
                )
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, maxLines = 1)
                Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2)
            }
            Spacer(Modifier.width(8.dp))
            trailing()
        }
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
