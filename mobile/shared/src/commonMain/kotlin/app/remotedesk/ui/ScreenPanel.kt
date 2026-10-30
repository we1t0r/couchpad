package app.remotedesk.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardHide
import androidx.compose.material.icons.automirrored.filled.Backspace
import androidx.compose.material.icons.automirrored.filled.KeyboardReturn
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import app.remotedesk.core.RemoteInput
import app.remotedesk.core.TextRelay
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.AwaitPointerEventScope
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.remotedesk.core.AgentConnection
import app.remotedesk.core.MouseButton
import app.remotedesk.core.PointerMath
import app.remotedesk.core.TouchpadSettings
import app.remotedesk.resources.Res
import app.remotedesk.resources.scr_connecting
import app.remotedesk.resources.scr_fullscreen
import app.remotedesk.resources.scr_fullscreen_exit
import app.remotedesk.resources.scr_hint
import app.remotedesk.resources.scr_monitor
import app.remotedesk.resources.scr_keyboard
import app.remotedesk.resources.scr_keyboard_hide
import app.remotedesk.resources.kb_field_hint
import app.remotedesk.resources.scr_sound_on
import app.remotedesk.resources.scr_sound_off
import app.remotedesk.core.openOpusPlayer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.jetbrains.compose.resources.decodeToImageBitmap
import org.jetbrains.compose.resources.stringResource
import kotlin.math.abs
import kotlin.math.sqrt

private const val LONG_PRESS_MS = 500L
private const val MAX_ZOOM = 5f

/**
 * Экран компьютера. Касание — щелчок в этой точке, долгое касание — правая кнопка,
 * ведёшь пальцем — перетаскивание; двумя пальцами: развести — увеличить, вести — прокрутка
 * (или сдвиг картинки, когда она увеличена).
 */
@Composable
fun ScreenPanel(
    connection: AgentConnection,
    settings: TouchpadSettings,
    onChangeSettings: ((TouchpadSettings) -> TouchpadSettings) -> Unit,
    immersive: Boolean,
    onImmersive: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    val info by connection.screenInfo.collectAsState()
    val frame by connection.screenFrame.collectAsState()
    val videoSize by connection.videoSize.collectAsState()
    val cursor by connection.cursor.collectAsState()
    val cursorShape by connection.cursorImage.collectAsState()
    var monitor by rememberSaveable { mutableStateOf<String?>(null) }
    var zoom by remember { mutableStateOf(1f) }
    var pan by remember { mutableStateOf(Offset.Zero) }
    var touched by rememberSaveable { mutableStateOf(false) }
    var typing by rememberSaveable { mutableStateOf(false) }
    val haptic = LocalHapticFeedback.current
    val current by rememberUpdatedState(settings)
    FullScreenMode(immersive)

    // Звук ПК: пока включён, пакеты Opus идут в проигрыватель телефона.
    if (settings.screenSound) {
        LaunchedEffect(connection, settings.mutePcSpeakers) {
            connection.startAudio(settings.mutePcSpeakers)
            val player = openOpusPlayer()
            try {
                withContext(Dispatchers.Default) { for (packet in connection.audio) player?.play(packet) }
            } finally {
                connection.stopAudio()
                player?.close()
            }
        }
    }

    val cursorBitmap by produceState<ImageBitmap?>(null, cursorShape) {
        value = cursorShape?.let { s -> withContext(Dispatchers.Default) { runCatching { s.png.decodeToImageBitmap() }.getOrNull() } }
    }
    // Декодируем только самый свежий кадр: пришёл новый — недоделанный старый бросаем.
    val image by produceState<ImageBitmap?>(null, frame) {
        val f = frame ?: return@produceState
        withContext(Dispatchers.Default) { runCatching { f.jpeg.decodeToImageBitmap() }.getOrNull() }?.let { value = it }
    }

    BoxWithConstraints(modifier.background(Color.Black)) {
        // Кадр чуть крупнее экрана телефона — чтобы при увеличении текст оставался читаемым.
        val maxWidth = (maxOf(constraints.maxWidth, constraints.maxHeight) * 1.25f).toInt().coerceIn(800, 1920)
        DisposableEffect(connection, monitor) {
            connection.startScreen(monitor, maxWidth)
            onDispose { connection.stopScreen() }
        }
        val box = Size(constraints.maxWidth.toFloat(), constraints.maxHeight.toFloat())
        val aspect = videoSize?.let { it.first.toFloat() / it.second }
            ?: frame?.let { it.width.toFloat() / it.height } ?: (16f / 9f)

        /** Точка касания → доля ширины и высоты монитора (0..1) с учётом увеличения; null — мимо картинки. */
        fun toScreen(p: Offset, clamp: Boolean): Offset? {
            val (w, h) = if (box.width / box.height > aspect) box.height * aspect to box.height else box.width to box.width / aspect
            val origin = Offset((box.width - w) / 2, (box.height - h) / 2)
            val center = Offset(box.width / 2, box.height / 2)
            val content = (p - center - pan) / zoom + center
            val x = (content.x - origin.x) / w
            val y = (content.y - origin.y) / h
            if (clamp) return Offset(x.coerceIn(0f, 1f), y.coerceIn(0f, 1f))
            return if (x in 0f..1f && y in 0f..1f) Offset(x, y) else null
        }

        fun clampPan(p: Offset, z: Float) = Offset(
            p.x.coerceIn(-box.width * (z - 1) / 2, box.width * (z - 1) / 2),
            p.y.coerceIn(-box.height * (z - 1) / 2, box.height * (z - 1) / 2),
        )

        val input = connection.input
        val buzz = { if (current.vibration) haptic.performHapticFeedback(HapticFeedbackType.LongPress) }

        Box(
            Modifier.fillMaxSize().pointerInput(connection, box, aspect) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    touched = true
                    val start = down.position
                    val slop = viewConfiguration.touchSlop
                    var mode = 0 // 0 — ещё не ясно, 1 — тащим мышью, 2 — увеличение, 3 — сдвиг картинки, 4 — прокрутка
                    var longFired = false
                    var travel = 0f
                    var pinchTravel = 0f
                    var lastSpread = 0f
                    var maxPointers = 1
                    try {
                        while (true) {
                            val event = if (mode == 0 && !longFired && maxPointers == 1) {
                                val left = LONG_PRESS_MS - (currentEventTime() - down.uptimeMillis)
                                withTimeoutOrNull(left.coerceAtLeast(1)) { awaitPointerEvent() }
                            } else {
                                awaitPointerEvent()
                            }
                            if (event == null) {
                                // Палец стоит на месте полсекунды — правая кнопка в этой точке.
                                longFired = true
                                toScreen(start, clamp = false)?.let {
                                    input.pointer(it.x, it.y)
                                    input.click(MouseButton.Right)
                                    buzz()
                                }
                                continue
                            }
                            val pressed = event.changes.filter { it.pressed }
                            if (pressed.isEmpty()) break
                            maxPointers = maxOf(maxPointers, pressed.size)
                            val moving = pressed.filter { it.previousPressed }
                            val delta = moving.average()
                            if (pressed.size == 1 && maxPointers == 1 && !longFired) {
                                travel += delta.getDistance()
                                if (mode == 0 && travel > slop) {
                                    toScreen(start, clamp = false)?.let {
                                        input.pointer(it.x, it.y)
                                        input.button(MouseButton.Left, true)
                                        mode = 1
                                    }
                                }
                                if (mode == 1) toScreen(pressed[0].position, clamp = true)?.let { input.pointer(it.x, it.y) }
                            } else if (pressed.size >= 2 && mode != 1) {
                                val spread = pressed.spread()
                                if (lastSpread > 0f) {
                                    travel += delta.getDistance()
                                    pinchTravel += abs(spread - lastSpread)
                                    if (mode == 0) {
                                        mode = when {
                                            pinchTravel > slop * 2 && pinchTravel > travel -> 2
                                            travel > slop -> if (zoom > 1.01f) 3 else 4
                                            else -> 0
                                        }
                                    }
                                    when (mode) {
                                        2 -> {
                                            val z = (zoom * spread / lastSpread).coerceIn(1f, MAX_ZOOM)
                                            pan = clampPan(pan + delta, z)
                                            zoom = z
                                        }
                                        3 -> pan = clampPan(pan + delta, zoom)
                                        4 -> {
                                            val (wx, wy) = PointerMath.scroll(delta.x / density, delta.y / density, current)
                                            input.scroll(wy, wx)
                                        }
                                    }
                                }
                                lastSpread = spread
                            }
                            event.changes.forEach { it.consume() }
                        }
                    } finally {
                        // Отпускаем кнопку и при отмене жеста — иначе на ПК «залипнет» ЛКМ.
                        if (mode == 1) input.button(MouseButton.Left, false)
                    }
                    if (mode == 0 && !longFired && maxPointers == 1) {
                        toScreen(start, clamp = false)?.let {
                            input.pointer(it.x, it.y)
                            input.click(MouseButton.Left)
                        }
                    }
                }
            },
        ) {
            val video = videoSize
            val picture = image
            if (video != null || picture != null) {
                Box(
                    Modifier.fillMaxSize().graphicsLayer {
                        scaleX = zoom
                        scaleY = zoom
                        translationX = pan.x
                        translationY = pan.y
                    },
                    contentAlignment = Alignment.Center,
                ) {
                    BoxWithConstraints(Modifier.aspectRatio(aspect)) {
                        if (video != null) {
                            VideoSurface(connection, Modifier.fillMaxSize())
                            // В видео курсора нет — рисуем его сами, в масштабе картинки.
                            val c = cursor
                            val shape = cursorShape
                            val bitmap = cursorBitmap
                            if (c != null && c.visible && shape != null && bitmap != null) {
                                val monitorWidth = (info?.current?.width ?: video.first).coerceAtLeast(1)
                                // this.maxWidth — размер картинки (снаружи есть своя maxWidth — ширина кадра в пикселях).
                                val boxWidth = this.maxWidth
                                val boxHeight = this.maxHeight
                                val scale = boxWidth / monitorWidth
                                val w = (scale * shape.width).coerceAtLeast(14.dp * shape.width / maxOf(shape.width, shape.height))
                                val k = w / shape.width
                                Image(
                                    bitmap = bitmap,
                                    contentDescription = null,
                                    contentScale = ContentScale.FillBounds,
                                    modifier = Modifier
                                        .offset(boxWidth * c.x - k * shape.hotX, boxHeight * c.y - k * shape.hotY)
                                        .size(w, k * shape.height),
                                )
                            }
                        } else if (picture != null) {
                            Image(picture, contentDescription = null, contentScale = ContentScale.FillBounds, modifier = Modifier.fillMaxSize())
                        }
                    }
                }
            } else Column(Modifier.align(Alignment.Center), horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator(color = Color.White)
                Text(stringResource(Res.string.scr_connecting), color = Color.White, modifier = Modifier.padding(top = 16.dp))
            }
        }

        // Кнопки поверх картинки: монитор и «во весь экран».
        Row(Modifier.align(Alignment.TopEnd).padding(8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            val monitors = info?.monitors.orEmpty()
            if (monitors.size > 1) {
                Box {
                    var open by remember { mutableStateOf(false) }
                    OverlayButton(Icons.Default.DesktopWindows, stringResource(Res.string.scr_monitor)) { open = true }
                    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                        monitors.forEach { m ->
                            DropdownMenuItem(
                                text = { Text("${m.name} · ${m.width}×${m.height}") },
                                onClick = {
                                    open = false
                                    zoom = 1f
                                    pan = Offset.Zero
                                    monitor = m.id
                                },
                                trailingIcon = if (m.id == info?.monitor) ({ Text("✓") }) else null,
                            )
                        }
                    }
                }
            }
            OverlayButton(
                if (settings.screenSound) Icons.AutoMirrored.Filled.VolumeUp else Icons.AutoMirrored.Filled.VolumeOff,
                stringResource(if (settings.screenSound) Res.string.scr_sound_off else Res.string.scr_sound_on),
            ) { onChangeSettings { it.copy(screenSound = !it.screenSound) } }
            OverlayButton(Icons.Default.Keyboard, stringResource(Res.string.scr_keyboard)) {
                typing = !typing
                touched = true
            }
            OverlayButton(
                if (immersive) Icons.Default.FullscreenExit else Icons.Default.Fullscreen,
                stringResource(if (immersive) Res.string.scr_fullscreen_exit else Res.string.scr_fullscreen),
            ) { onImmersive(!immersive) }
        }

        if (typing) TypingBar(connection.input, onClose = { typing = false }, Modifier.align(Alignment.BottomCenter))

        if (!touched && (image != null || videoSize != null)) {
            Text(
                stringResource(Res.string.scr_hint),
                color = Color.White,
                style = MaterialTheme.typography.bodySmall,
                textAlign = TextAlign.Center,
                modifier = Modifier.align(Alignment.BottomCenter).padding(16.dp)
                    .background(Color.Black.copy(alpha = 0.6f), RoundedCornerShape(12.dp)).padding(12.dp, 8.dp),
            )
        }
    }
}

/**
 * Строка ввода поверх экрана ПК: клавиатура телефона открывается сразу, набранное уходит на ПК по ходу набора.
 * ⌫ и Enter — отдельными кнопками: на пустом поле клавиатура телефона Backspace не присылает.
 */
@Composable
private fun TypingBar(input: RemoteInput, onClose: () -> Unit, modifier: Modifier) {
    val relay = remember(input) { TextRelay(input) }
    var text by remember { mutableStateOf("") }
    val focus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    LaunchedEffect(Unit) {
        focus.requestFocus()
        keyboard?.show()
    }
    fun enter() {
        input.keys("enter")
        relay.reset()
        text = ""
    }
    Surface(modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surfaceContainer) {
        Row(Modifier.padding(horizontal = 8.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                value = text,
                onValueChange = {
                    relay.onChange(it)
                    text = it
                },
                modifier = Modifier.weight(1f).focusRequester(focus),
                placeholder = { Text(stringResource(Res.string.kb_field_hint)) },
                singleLine = true,
                shape = MaterialTheme.shapes.large,
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Send),
                keyboardActions = KeyboardActions(onSend = { enter() }),
            )
            IconButton(onClick = {
                if (text.isEmpty()) {
                    input.keys("backspace")
                } else {
                    // Стираем и в поле, и на ПК — иначе они разойдутся.
                    text = text.dropLast(if (text.length > 1 && text[text.length - 2].isHighSurrogate()) 2 else 1)
                    relay.onChange(text)
                }
            }) { Icon(Icons.AutoMirrored.Filled.Backspace, contentDescription = "Backspace") }
            IconButton(onClick = { enter() }) { Icon(Icons.AutoMirrored.Filled.KeyboardReturn, contentDescription = "Enter") }
            IconButton(onClick = {
                keyboard?.hide()
                onClose()
            }) { Icon(Icons.Default.KeyboardHide, contentDescription = stringResource(Res.string.scr_keyboard_hide)) }
        }
    }
}

@Composable
private fun OverlayButton(icon: androidx.compose.ui.graphics.vector.ImageVector, description: String, onClick: () -> Unit) {
    FilledTonalIconButton(onClick = onClick, modifier = Modifier.size(44.dp)) {
        Icon(icon, contentDescription = description)
    }
}

private fun List<PointerInputChange>.average(): Offset {
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

private fun List<PointerInputChange>.spread(): Float {
    if (size < 2) return 0f
    val d = this[0].position - this[1].position
    return sqrt(d.x * d.x + d.y * d.y).coerceAtLeast(1f)
}

private fun AwaitPointerEventScope.currentEventTime(): Long = currentEvent.changes.firstOrNull()?.uptimeMillis ?: 0L
