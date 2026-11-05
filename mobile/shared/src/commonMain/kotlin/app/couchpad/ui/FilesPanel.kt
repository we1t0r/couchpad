package app.couchpad.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Apps
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.FolderZip
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.PictureAsPdf
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.TableChart
import androidx.compose.material.icons.filled.Upload
import androidx.compose.foundation.layout.size
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.couchpad.core.AgentException
import app.couchpad.core.AgentFiles
import app.couchpad.core.Conflict
import app.couchpad.core.FileError
import app.couchpad.core.PhoneStorage
import app.couchpad.core.RemoteEntry
import app.couchpad.core.RemoteRoot
import app.couchpad.core.Transfer
import app.couchpad.core.TransferState
import app.couchpad.core.Transfers
import app.couchpad.resources.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import org.jetbrains.compose.resources.getString
import org.jetbrains.compose.resources.stringResource

/** Что сделать с файлом или папкой — спрашиваем в диалоге. */
private sealed interface FileDialog {
    data class Actions(val entry: RemoteEntry) : FileDialog
    data class Rename(val entry: RemoteEntry) : FileDialog
    data class Delete(val entry: RemoteEntry) : FileDialog
    data object NewFolder : FileDialog
}

/** Файлы компьютера: папки, которые открыты телефону на ПК. */
@Composable
fun FilesPanel(files: AgentFiles, transfers: Transfers, storage: PhoneStorage, notify: suspend (String) -> Unit, modifier: Modifier = Modifier) {
    val scope = rememberCoroutineScope()
    var roots by remember { mutableStateOf<List<RemoteRoot>?>(null) }
    var root by remember { mutableStateOf<RemoteRoot?>(null) }
    var path by remember { mutableStateOf("") }
    var entries by remember { mutableStateOf<List<RemoteEntry>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var filter by remember { mutableStateOf("") }
    var dialog by remember { mutableStateOf<FileDialog?>(null) }
    var reload by remember { mutableIntStateOf(0) }

    suspend fun <T> guarded(block: suspend () -> T): T? = try {
        error = null
        block()
    } catch (e: CancellationException) {
        throw e
    } catch (e: FileError) {
        error = e.message
        null
    } catch (e: AgentException) {
        error = getString(Res.string.files_unreachable)
        null
    } catch (e: Exception) {
        error = e.message ?: e::class.simpleName
        null
    }

    LaunchedEffect(files, reload) {
        if (root == null) roots = guarded { files.roots() }
    }
    LaunchedEffect(files, root, path, reload) {
        val r = root ?: return@LaunchedEffect
        entries = null
        entries = guarded { files.list(r.id, path) }?.sortedWith(compareBy<RemoteEntry> { !it.dir }.thenBy { it.name.lowercase() })
    }
    // Отправка в эту папку закончилась — обновляем список.
    DisposableEffect(transfers) {
        transfers.onFinished = { t -> if (t.upload) reload++ }
        onDispose { transfers.onFinished = {} }
    }

    fun act(block: suspend () -> Unit) = scope.launch { guarded { block(); reload++ } }

    Column(modifier) {
        // Шапка: где мы и кнопки.
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            if (root != null) {
                IconButton(onClick = {
                    if (path.isEmpty()) root = null else path = AgentFiles.parent(path)
                    filter = ""
                }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(Res.string.action_back)) }
            }
            Text(
                root?.let { r -> if (path.isEmpty()) r.name else "${r.name} / ${path.replace("/", " / ")}" } ?: stringResource(Res.string.files_title),
                style = MaterialTheme.typography.titleMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f).padding(start = if (root == null) 12.dp else 0.dp),
            )
            IconButton(onClick = { reload++ }) { Icon(Icons.Default.Refresh, contentDescription = stringResource(Res.string.wol_recheck)) }
        }

        val r = root
        if (r != null) {
            Row(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = filter,
                    onValueChange = { filter = it },
                    placeholder = { Text(stringResource(Res.string.files_search)) },
                    leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                    shape = MaterialTheme.shapes.large,
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
            }
            if (r.writable) {
                Row(Modifier.padding(12.dp, 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilledTonalButton(onClick = {
                        scope.launch {
                            storage.pickFiles().forEach { transfers.upload(files, r.id, path, it) }
                        }
                    }) {
                        Icon(Icons.Default.Upload, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(Res.string.files_upload))
                    }
                    OutlinedButton(onClick = { dialog = FileDialog.NewFolder }) {
                        Icon(Icons.Default.CreateNewFolder, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(Res.string.files_new_folder))
                    }
                }
            } else {
                Text(
                    stringResource(Res.string.files_read_only),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(12.dp, 6.dp),
                )
            }
        }

        error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(12.dp))
        }

        Box(Modifier.weight(1f).fillMaxWidth()) {
            when {
                r == null -> {
                    val list = roots
                    if (list == null && error == null) Loading()
                    else LazyColumn {
                        items(list.orEmpty(), key = { it.id }) { item ->
                            Row(
                                Modifier.fillMaxWidth().clickable { root = item; path = "" }.padding(16.dp, 14.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                IconBadge(Icons.Default.Folder, size = 40.dp)
                                Spacer(Modifier.width(14.dp))
                                Column(Modifier.weight(1f)) {
                                    Text(item.name, style = MaterialTheme.typography.bodyLarge)
                                    Text(
                                        stringResource(if (item.writable) Res.string.files_rw else Res.string.files_ro),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                            }
                        }
                        item {
                            Text(
                                stringResource(Res.string.files_roots_hint),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(16.dp),
                            )
                        }
                    }
                }
                entries == null -> if (error == null) Loading()
                else -> {
                    val shown = entries.orEmpty().filter { filter.isBlank() || it.name.contains(filter.trim(), ignoreCase = true) }
                    if (shown.isEmpty()) {
                        Text(stringResource(Res.string.files_empty), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(16.dp))
                    }
                    LazyColumn {
                        items(shown, key = { it.name }) { e ->
                            Row(
                                Modifier.fillMaxWidth().clickable {
                                    if (e.dir) {
                                        path = AgentFiles.join(path, e.name)
                                        filter = ""
                                    } else {
                                        dialog = FileDialog.Actions(e)
                                    }
                                }.padding(16.dp, 12.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                if (e.dir) IconBadge(Icons.Default.Folder, size = 40.dp)
                                else IconBadge(fileIcon(e.name), MaterialTheme.colorScheme.onSurfaceVariant, MaterialTheme.colorScheme.surfaceContainerHigh, 40.dp)
                                Spacer(Modifier.width(14.dp))
                                Column(Modifier.weight(1f)) {
                                    Text(e.name, style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                    if (!e.dir) {
                                        Text(formatSize(e.size), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                    }
                                }
                                if (e.dir && r.writable) {
                                    IconButton(onClick = { dialog = FileDialog.Actions(e) }) { Icon(Icons.Default.MoreVert, contentDescription = null) }
                                }
                            }
                        }
                    }
                }
            }
        }

        TransfersList(transfers, storage)
    }

    val current = root
    when (val d = dialog) {
        null -> Unit
        is FileDialog.Actions -> {
            val e = d.entry
            val p = AgentFiles.join(path, e.name)
            AlertDialog(
                onDismissRequest = { dialog = null },
                title = { Text(e.name) },
                text = {
                    Column {
                        if (!e.dir) {
                            ActionRow(Icons.Default.Download, stringResource(Res.string.files_download)) {
                                dialog = null
                                transfers.download(files, current!!.id, p, e.name, e.size)
                            }
                            ActionRow(Icons.Default.DesktopWindows, stringResource(Res.string.files_open_pc)) {
                                dialog = null
                                act {
                                    files.openOnPc(current!!.id, p)
                                    notify(getString(Res.string.files_opened, e.name))
                                }
                            }
                        }
                        if (current?.writable == true) {
                            ActionRow(Icons.Default.Edit, stringResource(Res.string.files_rename)) { dialog = FileDialog.Rename(e) }
                            ActionRow(Icons.Default.Delete, stringResource(Res.string.files_delete)) { dialog = FileDialog.Delete(e) }
                        }
                    }
                },
                confirmButton = { TextButton(onClick = { dialog = null }) { Text(stringResource(Res.string.action_cancel)) } },
            )
        }
        is FileDialog.Rename -> NameDialog(
            title = stringResource(Res.string.files_rename),
            initial = d.entry.name,
            onDismiss = { dialog = null },
        ) { name ->
            dialog = null
            act { files.rename(current!!.id, AgentFiles.join(path, d.entry.name), name) }
        }
        is FileDialog.NewFolder -> NameDialog(
            title = stringResource(Res.string.files_new_folder),
            initial = "",
            onDismiss = { dialog = null },
        ) { name ->
            dialog = null
            act { files.mkdir(current!!.id, AgentFiles.join(path, name)) }
        }
        is FileDialog.Delete -> AlertDialog(
            onDismissRequest = { dialog = null },
            title = { Text(stringResource(Res.string.files_delete_title, d.entry.name)) },
            text = { Text(stringResource(Res.string.files_delete_text)) },
            confirmButton = {
                TextButton(onClick = {
                    dialog = null
                    act { files.delete(current!!.id, AgentFiles.join(path, d.entry.name)) }
                }) { Text(stringResource(Res.string.files_delete), color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { dialog = null }) { Text(stringResource(Res.string.action_cancel)) } },
        )
    }
}

@Composable
private fun Loading() {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
}

@Composable
private fun ActionRow(icon: ImageVector, text: String, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(16.dp))
        Text(text, style = MaterialTheme.typography.bodyLarge)
    }
}

@Composable
private fun NameDialog(title: String, initial: String, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true) },
        confirmButton = { TextButton(onClick = { onConfirm(name.trim()) }, enabled = name.isNotBlank() && name.trim() != initial) { Text("OK") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(Res.string.action_cancel)) } },
    )
}

/** Передачи: прогресс, ошибки, «Открыть» для скачанного и выбор при конфликте имён. */
@Composable
fun TransfersList(transfers: Transfers, storage: PhoneStorage) {
    val items by transfers.items.collectAsState()
    if (items.isEmpty()) return
    HorizontalDivider()
    Column(Modifier.fillMaxWidth().padding(12.dp, 8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(Res.string.files_transfers), style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
            if (items.any { it.state == TransferState.Done || it.state == TransferState.Failed }) {
                TextButton(onClick = transfers::clearFinished) { Text(stringResource(Res.string.files_clear)) }
            }
        }
        items.takeLast(4).forEach { t -> TransferRow(t, transfers) }
    }
}

@Composable
private fun TransferRow(t: Transfer, transfers: Transfers) {
    Column {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(if (t.upload) Icons.Default.Upload else Icons.Default.Download, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(t.name, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
            Text(
                when (t.state) {
                    TransferState.Done -> stringResource(Res.string.files_done)
                    TransferState.Failed -> stringResource(Res.string.files_failed)
                    else -> if (t.total > 0) "${t.done * 100 / t.total}%" else ""
                },
                style = MaterialTheme.typography.labelMedium,
            )
            if (t.state == TransferState.Done && !t.upload) {
                TextButton(onClick = { transfers.open(t) }) { Text(stringResource(Res.string.files_open)) }
            }
        }
        if (t.state == TransferState.Running || t.state == TransferState.Queued) {
            LinearProgressIndicator(
                progress = { if (t.total > 0) t.done.toFloat() / t.total else 0f },
                modifier = Modifier.fillMaxWidth().padding(top = 4.dp),
            )
        }
        t.error?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, color = if (t.state == TransferState.Failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (t.exists) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = { transfers.resolveConflict(t.id, Conflict.Rename) }) { Text(stringResource(Res.string.files_keep_both)) }
                TextButton(onClick = { transfers.resolveConflict(t.id, Conflict.Replace) }) { Text(stringResource(Res.string.files_replace)) }
            }
        }
    }
}

private fun fileIcon(name: String): ImageVector = when (name.substringAfterLast('.', "").lowercase()) {
    "jpg", "jpeg", "png", "gif", "webp", "heic", "bmp" -> Icons.Default.Image
    "mp4", "mkv", "avi", "mov", "webm" -> Icons.Default.Movie
    "mp3", "flac", "wav", "ogg", "m4a" -> Icons.Default.MusicNote
    "pdf" -> Icons.Default.PictureAsPdf
    "zip", "rar", "7z", "tar", "gz" -> Icons.Default.FolderZip
    "exe", "msi", "apk" -> Icons.Default.Apps
    "xls", "xlsx", "csv", "ods" -> Icons.Default.TableChart
    else -> Icons.Default.Description
}

fun formatSize(bytes: Long): String {
    if (bytes < 1024) return "$bytes Б"
    val units = listOf("КБ", "МБ", "ГБ", "ТБ")
    var value = bytes / 1024.0
    var unit = 0
    while (value >= 1024 && unit < units.lastIndex) {
        value /= 1024
        unit++
    }
    val rounded = if (value >= 100) value.toLong().toString() else ((value * 10).toLong() / 10.0).toString()
    return "$rounded ${units[unit]}"
}
