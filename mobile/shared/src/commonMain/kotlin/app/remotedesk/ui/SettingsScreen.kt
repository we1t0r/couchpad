package app.remotedesk.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Info
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
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
import androidx.compose.ui.unit.dp
import app.remotedesk.core.ButtonSize
import app.remotedesk.core.TouchpadSettings
import app.remotedesk.resources.*
import org.jetbrains.compose.resources.StringResource
import org.jetbrains.compose.resources.stringResource
import kotlin.math.roundToInt

/**
 * Настройки пульта: основные видны сразу, остальные — в разделе «Для опытных».
 * У каждой есть короткая подпись и «ⓘ» с подробным объяснением и значением по умолчанию.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    settings: TouchpadSettings,
    onChange: ((TouchpadSettings) -> TouchpadSettings) -> Unit,
    onReset: () -> Unit,
    onBack: () -> Unit,
) {
    var info by remember { mutableStateOf<Pair<StringResource, StringResource>?>(null) }
    var resetting by remember { mutableStateOf(false) }
    val defaults = TouchpadSettings()
    BackHandler(onBack)

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(Res.string.settings_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(Res.string.action_back))
                    }
                },
            )
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(vertical = 8.dp)) {
            Header(Res.string.settings_basic)
            SliderSetting(
                Res.string.s_sensitivity_title, Res.string.s_sensitivity_sub, Res.string.s_sensitivity_info,
                settings.sensitivity, 0.3f..3f, { info = it },
            ) { v -> onChange { it.copy(sensitivity = v) } }
            SliderSetting(
                Res.string.s_scroll_title, Res.string.s_scroll_sub, Res.string.s_scroll_info,
                settings.scrollSpeed, 0.3f..3f, { info = it },
            ) { v -> onChange { it.copy(scrollSpeed = v) } }
            SwitchSetting(Res.string.s_tap_title, Res.string.s_tap_sub, Res.string.s_tap_info, settings.tapToClick, { info = it }) { v ->
                onChange { it.copy(tapToClick = v) }
            }
            SwitchSetting(Res.string.s_vibration_title, Res.string.s_vibration_sub, Res.string.s_vibration_info, settings.vibration, { info = it }) { v ->
                onChange { it.copy(vibration = v) }
            }
            ButtonSizeSetting(settings.buttonSize, { info = it }) { v -> onChange { it.copy(buttonSize = v) } }
            SwitchSetting(Res.string.s_lefty_title, Res.string.s_lefty_sub, Res.string.s_lefty_info, settings.leftHanded, { info = it }) { v ->
                onChange { it.copy(leftHanded = v) }
            }
            SwitchSetting(Res.string.s_screen_title, Res.string.s_screen_sub, Res.string.s_screen_info, settings.keepScreenOn, { info = it }) { v ->
                onChange { it.copy(keepScreenOn = v) }
            }

            HorizontalDivider(Modifier.padding(vertical = 8.dp))
            Header(Res.string.settings_advanced)
            SliderSetting(
                Res.string.s_accel_title, Res.string.s_accel_sub, Res.string.s_accel_info,
                settings.acceleration, 0f..1f, { info = it }, percent = true,
            ) { v -> onChange { it.copy(acceleration = v) } }
            SwitchSetting(Res.string.s_invert_title, Res.string.s_invert_sub, Res.string.s_invert_info, settings.invertScroll, { info = it }) { v ->
                onChange { it.copy(invertScroll = v) }
            }
            SwitchSetting(Res.string.s_drag_title, Res.string.s_drag_sub, Res.string.s_drag_info, settings.tapAndDrag, { info = it }) { v ->
                onChange { it.copy(tapAndDrag = v) }
            }
            SwitchSetting(Res.string.s_middle_title, Res.string.s_middle_sub, Res.string.s_middle_info, settings.middleButton, { info = it }) { v ->
                onChange { it.copy(middleButton = v) }
            }
            SwitchSetting(Res.string.s_strip_title, Res.string.s_strip_sub, Res.string.s_strip_info, settings.scrollStrip, { info = it }) { v ->
                onChange { it.copy(scrollStrip = v) }
            }

            OutlinedButton(
                onClick = { resetting = true },
                enabled = settings != defaults,
                modifier = Modifier.padding(16.dp).fillMaxWidth(),
            ) { Text(stringResource(Res.string.settings_reset)) }
        }
    }

    info?.let { (title, text) ->
        AlertDialog(
            onDismissRequest = { info = null },
            title = { Text(stringResource(title)) },
            text = { Text(stringResource(text)) },
            confirmButton = { TextButton(onClick = { info = null }) { Text(stringResource(Res.string.action_ok)) } },
        )
    }

    if (resetting) {
        AlertDialog(
            onDismissRequest = { resetting = false },
            title = { Text(stringResource(Res.string.settings_reset)) },
            text = { Text(stringResource(Res.string.settings_reset_text)) },
            confirmButton = {
                TextButton(onClick = {
                    resetting = false
                    onReset()
                }) { Text(stringResource(Res.string.settings_reset_confirm)) }
            },
            dismissButton = { TextButton(onClick = { resetting = false }) { Text(stringResource(Res.string.action_cancel)) } },
        )
    }
}

@Composable
private fun Header(text: StringResource) {
    Text(
        stringResource(text),
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
    )
}

@Composable
private fun SettingRow(
    title: StringResource,
    subtitle: StringResource,
    details: StringResource,
    onInfo: (Pair<StringResource, StringResource>) -> Unit,
    onClick: (() -> Unit)? = null,
    trailing: @Composable () -> Unit = {},
) {
    Row(
        Modifier.fillMaxWidth().let { if (onClick != null) it.clickable(onClick = onClick) else it }.padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(stringResource(title), style = MaterialTheme.typography.bodyLarge)
            Text(stringResource(subtitle), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        IconButton(onClick = { onInfo(title to details) }) {
            Icon(Icons.Default.Info, contentDescription = stringResource(Res.string.settings_more), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        trailing()
    }
}

@Composable
private fun SwitchSetting(
    title: StringResource,
    subtitle: StringResource,
    details: StringResource,
    value: Boolean,
    onInfo: (Pair<StringResource, StringResource>) -> Unit,
    onValue: (Boolean) -> Unit,
) {
    SettingRow(title, subtitle, details, onInfo, onClick = { onValue(!value) }) {
        Switch(checked = value, onCheckedChange = onValue)
    }
}

@Composable
private fun SliderSetting(
    title: StringResource,
    subtitle: StringResource,
    details: StringResource,
    value: Float,
    range: ClosedFloatingPointRange<Float>,
    onInfo: (Pair<StringResource, StringResource>) -> Unit,
    percent: Boolean = false,
    onValue: (Float) -> Unit,
) {
    Column {
        SettingRow(title, subtitle, details, onInfo) {
            Text(
                if (percent) "${(value * 100).roundToInt()}%" else "×${(value * 10).roundToInt() / 10.0}",
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.padding(end = 8.dp),
            )
        }
        Slider(
            value = value,
            onValueChange = { onValue((it * 20).roundToInt() / 20f) },
            valueRange = range,
            modifier = Modifier.padding(horizontal = 16.dp),
        )
    }
}

@Composable
private fun ButtonSizeSetting(
    value: ButtonSize,
    onInfo: (Pair<StringResource, StringResource>) -> Unit,
    onValue: (ButtonSize) -> Unit,
) {
    Column {
        SettingRow(Res.string.s_buttons_title, Res.string.s_buttons_sub, Res.string.s_buttons_info, onInfo)
        Row(Modifier.padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf(
                ButtonSize.Small to Res.string.size_small,
                ButtonSize.Medium to Res.string.size_medium,
                ButtonSize.Large to Res.string.size_large,
            ).forEach { (size, label) ->
                FilterChip(selected = value == size, onClick = { onValue(size) }, label = { Text(stringResource(label)) })
            }
        }
    }
}
