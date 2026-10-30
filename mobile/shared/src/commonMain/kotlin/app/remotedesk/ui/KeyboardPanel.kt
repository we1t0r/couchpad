package app.remotedesk.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Backspace
import androidx.compose.material.icons.automirrored.filled.KeyboardReturn
import androidx.compose.material.icons.automirrored.filled.VolumeDown
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowLeft
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.SpaceBar
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.AssistChip
import androidx.compose.material3.AssistChipDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.remotedesk.core.AgentConnection
import app.remotedesk.core.AgentFiles
import app.remotedesk.core.ClipImage
import app.remotedesk.core.FileError
import app.remotedesk.core.PhoneClipboard
import app.remotedesk.core.TextRelay
import app.remotedesk.core.TouchpadSettings
import app.remotedesk.resources.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import org.jetbrains.compose.resources.StringResource
import org.jetbrains.compose.resources.getString
import org.jetbrains.compose.resources.stringResource

/** Готовое сочетание клавиш с понятным названием. */
private class Shortcut(val label: StringResource, val keys: List<String>)

private val shortcuts = listOf(
    Shortcut(Res.string.sc_copy, listOf("ctrl", "c")),
    Shortcut(Res.string.sc_paste, listOf("ctrl", "v")),
    Shortcut(Res.string.sc_cut, listOf("ctrl", "x")),
    Shortcut(Res.string.sc_undo, listOf("ctrl", "z")),
    Shortcut(Res.string.sc_redo, listOf("ctrl", "y")),
    Shortcut(Res.string.sc_select_all, listOf("ctrl", "a")),
    Shortcut(Res.string.sc_alt_tab, listOf("alt", "tab")),
    Shortcut(Res.string.sc_close, listOf("alt", "f4")),
    Shortcut(Res.string.sc_desktop, listOf("win", "d")),
    Shortcut(Res.string.sc_explorer, listOf("win", "e")),
    Shortcut(Res.string.sc_start, listOf("win")),
    Shortcut(Res.string.sc_task_manager, listOf("ctrl", "shift", "esc")),
    Shortcut(Res.string.sc_screenshot, listOf("win", "shift", "s")),
    Shortcut(Res.string.sc_refresh, listOf("f5")),
    Shortcut(Res.string.sc_fullscreen, listOf("f11")),
)

private val media = listOf(
    Icons.Default.SkipPrevious to "prevtrack", Icons.Default.PlayArrow to "playpause", Icons.Default.SkipNext to "nexttrack",
    Icons.AutoMirrored.Filled.VolumeOff to "volumemute", Icons.AutoMirrored.Filled.VolumeDown to "volumedown", Icons.AutoMirrored.Filled.VolumeUp to "volumeup",
)

private val modifierKeys = listOf("ctrl", "alt", "shift", "win")

/** Клавиша на экране: подпись или значок. */
private class Key(val label: String, val key: String, val icon: ImageVector? = null, val weight: Float = 1f)

private val keyRows = listOf(
    listOf(Key("Esc", "esc"), Key("Tab", "tab"), Key("Home", "home"), Key("↑", "up", Icons.Default.KeyboardArrowUp), Key("End", "end"), Key("PgUp", "pageup")),
    listOf(Key("Del", "delete"), Key("⌫", "backspace", Icons.AutoMirrored.Filled.Backspace), Key("←", "left", Icons.Default.KeyboardArrowLeft), Key("↓", "down", Icons.Default.KeyboardArrowDown), Key("→", "right", Icons.Default.KeyboardArrowRight), Key("PgDn", "pagedown")),
    listOf(Key("Ins", "insert"), Key("PrtSc", "printscreen"), Key("☰", "menu", Icons.Default.Menu), Key("Space", "space", Icons.Default.SpaceBar, 1.5f), Key("Enter", "enter", Icons.AutoMirrored.Filled.KeyboardReturn, 1.5f)),
)

/** Клавиатура: ввод текста, буфер обмена, спецклавиши, залипающие модификаторы, сочетания и медиа. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun KeyboardPanel(
    connection: AgentConnection,
    settings: TouchpadSettings,
    clipboard: PhoneClipboard,
    files: AgentFiles?,
    clipHistory: List<String>,
    addClip: (String) -> Unit,
    notify: suspend (String) -> Unit,
    modifier: Modifier = Modifier,
    showClipboard: Boolean = true,
) {
    val input = connection.input
    val relay = remember(input) { TextRelay(input) }
    val scope = rememberCoroutineScope()

    fun fromPc() = scope.launch {
        try {
            val clip = connection.clipboardGet()
            when {
                clip.text.isNotEmpty() -> {
                    clipboard.setText(clip.text)
                    addClip(clip.text)
                    notify(getString(Res.string.clip_got))
                }
                clip.image && files != null -> {
                    clipboard.setImage(ClipImage(files.clipImage(), "image/png"))
                    notify(getString(Res.string.clip_image_got))
                }
                else -> notify(getString(Res.string.clip_pc_empty))
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: FileError) {
            notify(e.message ?: getString(Res.string.clip_failed))
        } catch (_: Exception) {
            notify(getString(Res.string.clip_failed))
        }
    }

    /** Текст из истории — сразу; без него — что лежит в буфере телефона: текст или картинка. */
    fun toPc(text: String?) = scope.launch {
        try {
            val sendText = text ?: clipboard.text()
            if (!sendText.isNullOrEmpty()) {
                connection.clipboardSet(sendText)
                addClip(sendText)
                notify(getString(Res.string.clip_sent))
                return@launch
            }
            val image = if (text == null) clipboard.image() else null
            if (image == null || files == null) {
                notify(getString(Res.string.clip_phone_empty))
                return@launch
            }
            files.putClipImage(image)
            notify(getString(Res.string.clip_image_sent))
        } catch (e: CancellationException) {
            throw e
        } catch (e: FileError) {
            notify(e.message ?: getString(Res.string.clip_failed))
        } catch (_: Exception) {
            notify(getString(Res.string.clip_failed))
        }
    }
    var text by remember { mutableStateOf("") }
    // Залипающие модификаторы: нажали Ctrl — следующая клавиша уйдёт вместе с ним.
    var latched by remember { mutableStateOf(emptySet<String>()) }
    val haptic = LocalHapticFeedback.current
    val buzz = { if (settings.vibration) haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove) }

    fun press(key: String) {
        val mods = modifierKeys.filter { it in latched }
        input.keys(mods + key)
        latched = emptySet()
        buzz()
    }

    Column(
        modifier.verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        OutlinedTextField(
            value = text,
            onValueChange = { new ->
                // С зажатым модификатором буква — это сочетание (Ctrl+C), а не текст.
                val added = if (new.length == text.length + 1 && new.startsWith(text)) new.last() else null
                if (latched.isNotEmpty() && added != null && added.code < 128 && added.isLetterOrDigit()) {
                    press(added.lowercaseChar().toString())
                } else {
                    relay.onChange(new)
                    text = new
                }
            },
            modifier = Modifier.fillMaxWidth(),
            label = { Text(stringResource(Res.string.kb_field)) },
            placeholder = { Text(stringResource(Res.string.kb_field_hint)) },
            leadingIcon = { Icon(Icons.Default.Keyboard, contentDescription = null) },
            singleLine = true,
            shape = MaterialTheme.shapes.large,
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(onSend = {
                input.keys("enter")
                relay.reset()
                text = ""
            }),
            trailingIcon = {
                if (text.isNotEmpty()) {
                    IconButton(onClick = {
                        relay.reset()
                        text = ""
                    }) { Icon(Icons.Default.Clear, contentDescription = stringResource(Res.string.kb_clear)) }
                }
            },
        )
        Hint(stringResource(Res.string.kb_field_explain), Modifier.padding(horizontal = 4.dp))

        SectionTitle(stringResource(Res.string.kb_keys))
        Panel {
            // Ctrl, Alt, Shift, Win — нажал, и они сработают вместе со следующей клавишей.
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                listOf("ctrl" to "Ctrl", "alt" to "Alt", "shift" to "Shift", "win" to "Win").forEach { (key, label) ->
                    FilterChip(
                        selected = key in latched,
                        onClick = { latched = if (key in latched) latched - key else latched + key },
                        label = { Text(label, modifier = Modifier.fillMaxWidth(), textAlign = androidx.compose.ui.text.style.TextAlign.Center) },
                        modifier = Modifier.weight(1f),
                    )
                }
            }
            Hint(stringResource(Res.string.kb_modifiers))
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                keyRows.forEach { row ->
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        row.forEach { k -> KeyButton(k.label, k.icon, Modifier.weight(k.weight)) { press(k.key) } }
                    }
                }
                // F1–F12 — двумя рядами по шесть: все видны сразу, без прокрутки вбок.
                listOf(1..6, 7..12).forEach { keys ->
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        keys.forEach { n -> KeyButton("F$n", null, Modifier.weight(1f)) { press("f$n") } }
                    }
                }
            }
        }

        if (showClipboard) {
            SectionTitle(stringResource(Res.string.kb_clipboard))
            Panel {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilledTonalButton(onClick = { fromPc() }, modifier = Modifier.weight(1f), contentPadding = PaddingValues(vertical = 12.dp)) {
                        Icon(Icons.Default.Download, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(Res.string.clip_from_pc))
                    }
                    FilledTonalButton(
                        onClick = { toPc(null) },
                        modifier = Modifier.weight(1f),
                        contentPadding = PaddingValues(vertical = 12.dp),
                    ) {
                        Icon(Icons.Default.Upload, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(Res.string.clip_to_pc))
                    }
                }
                if (clipHistory.isNotEmpty()) {
                    Hint(stringResource(Res.string.clip_history))
                    clipHistory.forEach { item ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Row(
                                Modifier.weight(1f).clickable {
                                    clipboard.setText(item)
                                    scope.launch { notify(getString(Res.string.clip_copied)) }
                                }.padding(vertical = 8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Icon(Icons.Default.ContentCopy, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(18.dp))
                                Spacer(Modifier.width(12.dp))
                                Text(item.replace('\n', ' '), maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)
                            }
                            IconButton(onClick = { toPc(item) }) {
                                Icon(Icons.Default.Upload, contentDescription = stringResource(Res.string.clip_to_pc), tint = MaterialTheme.colorScheme.primary)
                            }
                        }
                    }
                }
            }

        }
        SectionTitle(stringResource(Res.string.kb_shortcuts))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            shortcuts.forEach { sc ->
                AssistChip(
                    onClick = {
                        input.keys(sc.keys)
                        buzz()
                    },
                    label = { Text(stringResource(sc.label)) },
                    colors = AssistChipDefaults.assistChipColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
                )
            }
        }

        SectionTitle(stringResource(Res.string.kb_media))
        Panel {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                media.forEach { (icon, key) -> KeyButton("", icon, Modifier.weight(1f)) { press(key) } }
            }
        }
    }
}

@Composable
private fun KeyButton(label: String, icon: ImageVector?, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Surface(
        onClick = onClick,
        modifier = modifier.height(46.dp),
        shape = MaterialTheme.shapes.small,
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
    ) {
        Box(contentAlignment = Alignment.Center) {
            if (icon != null) Icon(icon, contentDescription = label.ifEmpty { null }, modifier = Modifier.size(22.dp))
            else Text(label, maxLines = 1, style = MaterialTheme.typography.labelLarge)
        }
    }
}
