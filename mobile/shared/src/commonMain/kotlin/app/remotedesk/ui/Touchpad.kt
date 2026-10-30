package app.remotedesk.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material.icons.filled.UnfoldMore
import androidx.compose.material3.Icon
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.remotedesk.core.MouseButton
import app.remotedesk.core.PointerMath
import app.remotedesk.core.RemoteInput
import app.remotedesk.core.TouchpadSettings
import app.remotedesk.resources.Res
import app.remotedesk.resources.button_left
import app.remotedesk.resources.button_middle
import app.remotedesk.resources.button_right
import app.remotedesk.resources.scroll_strip
import app.remotedesk.resources.touchpad_hint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.jetbrains.compose.resources.stringResource
import kotlin.math.abs
import kotlin.math.ln
import kotlin.math.sqrt

private const val TAP_MS = 220L
private const val DOUBLE_TAP_MS = 260L

/** Тачпад целиком: поверхность, полоса прокрутки сбоку и кнопки мыши внизу. */
@Composable
fun TouchpadPanel(input: RemoteInput, settings: TouchpadSettings, modifier: Modifier = Modifier) {
    val haptic = LocalHapticFeedback.current
    val buzz: () -> Unit = { if (settings.vibration) haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove) }

    Column(modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(Modifier.weight(1f).fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            if (settings.scrollStrip && settings.leftHanded) ScrollStrip(input, settings)
            TouchSurface(input, settings, buzz, Modifier.weight(1f).fillMaxHeight())
            if (settings.scrollStrip && !settings.leftHanded) ScrollStrip(input, settings)
        }
        Row(Modifier.fillMaxWidth().height(settings.buttonSize.heightDp.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            val buttons = buildList {
                add(MouseButton.Left)
                if (settings.middleButton) add(MouseButton.Middle)
                add(MouseButton.Right)
            }.let { if (settings.leftHanded) it.reversed() else it }
            buttons.forEach { button ->
                MouseButtonPad(input, button, buzz, Modifier.weight(if (button == MouseButton.Middle) 0.6f else 1f).fillMaxHeight())
            }
        }
    }
}

@Composable
private fun TouchSurface(input: RemoteInput, settings: TouchpadSettings, buzz: () -> Unit, modifier: Modifier) {
    val scope = rememberCoroutineScope()
    val gestures = remember { TouchGestures(scope) }
    val current by rememberUpdatedState(settings)
    val currentBuzz by rememberUpdatedState(buzz)
    val hint = stringResource(Res.string.touchpad_hint)

    Box(
        modifier
            .background(MaterialTheme.colorScheme.surfaceContainer, RoundedCornerShape(24.dp))
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(24.dp))
            .semantics { contentDescription = hint }
            .pointerInput(input) { with(gestures) { run(input, { current }, { currentBuzz() }) } },
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.padding(40.dp)) {
            Icon(
                Icons.Default.TouchApp,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.35f),
                modifier = Modifier.size(40.dp),
            )
            Spacer(Modifier.height(12.dp))
            Text(
                hint,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.6f),
                textAlign = TextAlign.Center,
            )
        }
    }
}

/**
 * Жесты тачпада:
 * тап — ЛКМ, двойной тап с удержанием — перетаскивание, два пальца — прокрутка, тап двумя — ПКМ,
 * тап тремя — СКМ, щипок — масштаб (Ctrl+колесо), три пальца влево/вправо — переключение окон (Alt+Tab).
 */
private class TouchGestures(private val scope: CoroutineScope) {
    private enum class Mode { None, Move, Drag, Scroll, Pinch, Switch }

    private var pendingClick: Job? = null
    private var lastTapUp = 0L

    suspend fun androidx.compose.ui.input.pointer.PointerInputScope.run(
        input: RemoteInput,
        settings: () -> TouchpadSettings,
        buzz: () -> Unit,
    ) {
        awaitEachGesture {
            val first = awaitFirstDown(requireUnconsumed = false)
            val s = settings()
            val slop = viewConfiguration.touchSlop
            val downAt = first.uptimeMillis
            // Второе касание вскоре после тапа: ждущий щелчок ещё не отправлен.
            val secondTap = pendingClick?.isActive == true && downAt - lastTapUp < DOUBLE_TAP_MS
            pendingClick?.cancel()
            pendingClick = null
            var owedClick = secondTap

            var mode = Mode.None
            var maxPointers = 1
            var travel = 0f
            var pinchTravel = 0f
            var switchAcc = 0f
            var altHeld = false
            var zoomAcc = 0f
            var lastDistance = 0f

            fun payOwedClick() {
                if (owedClick) {
                    input.click(MouseButton.Left)
                    owedClick = false
                }
            }

            try {
                while (true) {
                    val event = awaitPointerEvent()
                    val pressed = event.changes.filter { it.pressed }
                    if (pressed.isEmpty()) break
                    maxPointers = maxOf(maxPointers, pressed.size)
                    val moving = pressed.filter { it.previousPressed }
                    val delta = moving.averageChange()
                    val dtMs = moving.firstOrNull()?.let { it.uptimeMillis - it.previousUptimeMillis } ?: 0L
                    val dxDp = delta.x / density
                    val dyDp = delta.y / density

                    when (pressed.size) {
                        1 -> if (mode == Mode.None || mode == Mode.Move || mode == Mode.Drag) {
                            travel += delta.getDistance()
                            if (mode == Mode.None && travel > slop) {
                                mode = if (owedClick && s.tapAndDrag) {
                                    owedClick = false
                                    input.button(MouseButton.Left, true)
                                    buzz()
                                    Mode.Drag
                                } else {
                                    payOwedClick()
                                    Mode.Move
                                }
                            }
                            if (mode != Mode.None) {
                                val (x, y) = PointerMath.cursor(dxDp, dyDp, dtMs, s)
                                input.move(x, y)
                            }
                        }
                        2 -> {
                            val distance = pressed.spread()
                            if (lastDistance > 0f && mode != Mode.Drag) {
                                travel += delta.getDistance()
                                pinchTravel += abs(distance - lastDistance)
                                if (mode == Mode.None || mode == Mode.Move) {
                                    if (pinchTravel > slop * 2 && pinchTravel > travel) {
                                        payOwedClick()
                                        mode = Mode.Pinch
                                        input.key("ctrl", true)
                                    } else if (travel > slop) {
                                        payOwedClick()
                                        mode = Mode.Scroll
                                    }
                                }
                                when (mode) {
                                    Mode.Scroll -> {
                                        val (wx, wy) = PointerMath.scroll(dxDp, dyDp, s)
                                        input.scroll(wy, wx)
                                    }
                                    Mode.Pinch -> {
                                        // Разводим пальцы — приближаем (колесо вверх с Ctrl).
                                        zoomAcc += ln(distance / lastDistance) * 6f
                                        val notches = zoomAcc.toInt()
                                        if (notches != 0) {
                                            input.scroll(notches.toFloat())
                                            zoomAcc -= notches
                                        }
                                    }
                                    else -> Unit
                                }
                            }
                            lastDistance = distance
                        }
                        else -> {
                            if (mode == Mode.None || mode == Mode.Move || mode == Mode.Switch) {
                                travel += delta.getDistance()
                                if (mode != Mode.Switch && travel > slop * 2) {
                                    payOwedClick()
                                    mode = Mode.Switch
                                }
                                if (mode == Mode.Switch) {
                                    switchAcc += dxDp
                                    while (abs(switchAcc) >= PointerMath.DP_PER_WINDOW) {
                                        if (!altHeld) {
                                            input.key("alt", true)
                                            altHeld = true
                                        }
                                        val forward = switchAcc > 0
                                        input.keys(if (forward) listOf("alt", "tab") else listOf("alt", "shift", "tab"))
                                        switchAcc -= if (forward) PointerMath.DP_PER_WINDOW else -PointerMath.DP_PER_WINDOW
                                        buzz()
                                    }
                                }
                            }
                        }
                    }
                    if (pressed.size != 2) lastDistance = 0f
                    event.changes.forEach { it.consume() }
                }
            } finally {
                // Отпускаем зажатое и при отмене жеста — иначе на ПК «залипнет» ЛКМ, Ctrl или Alt.
                when (mode) {
                    Mode.Drag -> input.button(MouseButton.Left, false)
                    Mode.Pinch -> input.key("ctrl", false)
                    Mode.Switch -> if (altHeld) input.key("alt", false)
                    else -> Unit
                }
            }

            val upAt = currentEventTimeMillis()

            val quick = upAt - downAt < TAP_MS * if (maxPointers > 1) 2 else 1
            if (mode == Mode.None && quick && s.tapToClick) {
                when (maxPointers) {
                    1 -> if (owedClick) {
                        // Два быстрых тапа — двойной щелчок.
                        owedClick = false
                        input.click(MouseButton.Left, 2)
                        buzz()
                    } else if (s.tapAndDrag) {
                        lastTapUp = upAt
                        pendingClick = scope.launch {
                            delay(DOUBLE_TAP_MS)
                            input.click(MouseButton.Left)
                        }
                        buzz()
                    } else {
                        input.click(MouseButton.Left)
                        buzz()
                    }
                    2 -> {
                        input.click(MouseButton.Right)
                        buzz()
                    }
                    else -> {
                        input.click(MouseButton.Middle)
                        buzz()
                    }
                }
            }
            payOwedClick()
        }
    }

    private fun androidx.compose.ui.input.pointer.AwaitPointerEventScope.currentEventTimeMillis(): Long =
        currentEvent.changes.firstOrNull()?.uptimeMillis ?: 0L
}

private fun List<PointerInputChange>.averageChange(): Offset {
    if (isEmpty()) return Offset.Zero
    var x = 0f
    var y = 0f
    forEach {
        val d = it.positionChange()
        x += d.x
        y += d.y
    }
    return Offset(x / size, y / size)
}

/** Расстояние между первыми двумя пальцами. */
private fun List<PointerInputChange>.spread(): Float {
    if (size < 2) return 0f
    val d = this[0].position - this[1].position
    return sqrt(d.x * d.x + d.y * d.y).coerceAtLeast(1f)
}

@Composable
private fun ScrollStrip(input: RemoteInput, settings: TouchpadSettings) {
    val current by rememberUpdatedState(settings)
    val label = stringResource(Res.string.scroll_strip)
    Box(
        Modifier
            .width(44.dp)
            .fillMaxHeight()
            .background(MaterialTheme.colorScheme.surfaceContainerLow, RoundedCornerShape(24.dp))
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(24.dp))
            .semantics { contentDescription = label }
            .pointerInput(input) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    while (true) {
                        val event = awaitPointerEvent()
                        val change = event.changes.firstOrNull { it.pressed } ?: break
                        val (_, wy) = PointerMath.scroll(0f, change.positionChange().y / density, current)
                        input.scroll(wy)
                        change.consume()
                    }
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Icon(Icons.Default.UnfoldMore, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Кнопка мыши: нажата, пока на ней палец, — так можно держать ЛКМ и тащить вторым пальцем по тачпаду. */
@Composable
private fun MouseButtonPad(input: RemoteInput, button: MouseButton, buzz: () -> Unit, modifier: Modifier) {
    var pressed by remember { mutableStateOf(false) }
    val currentBuzz by rememberUpdatedState(buzz)
    val label = stringResource(
        when (button) {
            MouseButton.Left -> Res.string.button_left
            MouseButton.Right -> Res.string.button_right
            MouseButton.Middle -> Res.string.button_middle
        },
    )
    val colors = MaterialTheme.colorScheme
    Box(
        modifier
            .background(if (pressed) colors.primary else colors.surfaceContainerHigh, RoundedCornerShape(20.dp))
            .semantics { contentDescription = label }
            .pointerInput(input, button) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false).consume()
                    pressed = true
                    input.button(button, true)
                    currentBuzz()
                    try {
                        do {
                            val event = awaitPointerEvent()
                            event.changes.forEach { it.consume() }
                        } while (event.changes.any { it.pressed })
                    } finally {
                        // И при отмене жеста (например, экран повернулся) кнопку обязательно отпускаем.
                        pressed = false
                        input.button(button, false)
                    }
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Text(label, style = MaterialTheme.typography.titleMedium, color = if (pressed) colors.onPrimary else colors.onSurface)
    }
}
