package app.remotedesk.ui

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material3.AssistChip
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import app.remotedesk.core.RemoteInput
import app.remotedesk.core.TextRelay
import app.remotedesk.core.TouchpadSettings
import app.remotedesk.resources.*
import org.jetbrains.compose.resources.StringResource
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
    "⏮" to "prevtrack", "⏯" to "playpause", "⏭" to "nexttrack",
    "🔇" to "volumemute", "🔉" to "volumedown", "🔊" to "volumeup",
)

private val modifierKeys = listOf("ctrl", "alt", "shift", "win")

/** Клавиатура: ввод текста, спецклавиши, залипающие модификаторы, сочетания и медиа. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun KeyboardPanel(input: RemoteInput, settings: TouchpadSettings, modifier: Modifier = Modifier) {
    val relay = remember(input) { TextRelay(input) }
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
        modifier.verticalScroll(rememberScrollState()).padding(12.dp),
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
            singleLine = true,
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
        Text(stringResource(Res.string.kb_field_explain), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

        Section(Res.string.kb_modifiers) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf("ctrl" to "Ctrl", "alt" to "Alt", "shift" to "Shift", "win" to "Win").forEach { (key, label) ->
                    FilterChip(
                        selected = key in latched,
                        onClick = { latched = if (key in latched) latched - key else latched + key },
                        label = { Text(label) },
                    )
                }
            }
        }

        Section(Res.string.kb_keys) {
            KeyGrid(
                listOf(
                    listOf("Esc" to "esc", "Tab" to "tab", "Home" to "home", "↑" to "up", "End" to "end", "PgUp" to "pageup"),
                    listOf("Del" to "delete", "⌫" to "backspace", "←" to "left", "↓" to "down", "→" to "right", "PgDn" to "pagedown"),
                    listOf("Enter" to "enter", "Space" to "space", "Ins" to "insert", "PrtSc" to "printscreen", "☰" to "menu"),
                ),
                ::press,
            )
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                (1..12).forEach { n -> KeyButton("F$n") { press("f$n") } }
            }
        }

        Section(Res.string.kb_shortcuts) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                shortcuts.forEach { sc ->
                    AssistChip(
                        onClick = {
                            input.keys(sc.keys)
                            buzz()
                        },
                        label = { Text(stringResource(sc.label)) },
                    )
                }
            }
        }

        Section(Res.string.kb_media) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                media.forEach { (label, key) ->
                    FilledTonalButton(onClick = { press(key) }, modifier = Modifier.weight(1f)) { Text(label) }
                }
            }
        }
    }
}

@Composable
private fun Section(title: StringResource, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(stringResource(title), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
        content()
    }
}

@Composable
private fun KeyGrid(rows: List<List<Pair<String, String>>>, onKey: (String) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        rows.forEach { row ->
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                row.forEach { (label, key) -> KeyButton(label, Modifier.weight(1f)) { onKey(key) } }
            }
        }
    }
}

@Composable
private fun KeyButton(label: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    FilledTonalButton(
        onClick = onClick,
        modifier = modifier.height(48.dp),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 6.dp),
    ) { Text(label, maxLines = 1) }
}
