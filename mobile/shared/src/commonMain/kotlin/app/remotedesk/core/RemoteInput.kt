package app.remotedesk.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonPrimitive

enum class MouseButton(internal val wire: String) { Left("left"), Right("right"), Middle("middle") }

/**
 * Ввод на ПК (см. «Ввод» в protocol/PROTOCOL.md). Отправка не ждёт ответа.
 * [send] получает сообщение и признак «можно выбросить, если сеть не успевает» (движения и прокрутка).
 */
class RemoteInput(private val sink: (text: String, droppable: Boolean) -> Unit) {
    private fun send(text: String, droppable: Boolean = false) = sink(text, droppable)

    fun move(dx: Float, dy: Float) {
        if (dx != 0f || dy != 0f) send(message("move", "dx" to dx.round(), "dy" to dy.round()), droppable = true)
    }

    fun button(button: MouseButton, down: Boolean) = send(message("button", "b" to button.wire, "down" to down))

    fun click(button: MouseButton = MouseButton.Left, count: Int = 1) =
        send(message("click", "b" to button.wire, "n" to count))

    /** В щелчках колеса: [dy] > 0 — вверх, [dx] > 0 — вправо. */
    fun scroll(dy: Float, dx: Float = 0f) {
        if (dx != 0f || dy != 0f) send(message("scroll", "dy" to dy.round(), "dx" to dx.round()), droppable = true)
    }

    fun key(name: String, down: Boolean) = send(message("key", "k" to name, "down" to down))

    /** Сочетание: нажать по порядку, отпустить в обратном. */
    fun keys(vararg names: String) = keys(names.toList())

    fun keys(names: List<String>) = send(message("keys", "k" to JsonArray(names.map(::JsonPrimitive))))

    fun text(text: String) {
        if (text.isNotEmpty()) send(message("text", "s" to text))
    }

    // Три знака после запятой — достаточно, и сообщения короче.
    private fun Float.round(): Double = kotlin.math.round(this * 1000.0) / 1000.0
}

/**
 * Превращает изменения поля ввода в нажатия на ПК: дописанное — печатаем, стёртое — Backspace.
 * Так работают любые клавиатуры телефона, автозамена и голосовой ввод.
 */
class TextRelay(private val input: RemoteInput) {
    private var sent = ""

    fun onChange(new: String) {
        var common = 0
        val max = minOf(sent.length, new.length)
        while (common < max && sent[common] == new[common]) common++
        // Не режем эмодзи пополам.
        if (common > 0 && sent[common - 1].isHighSurrogate()) common--
        val erased = codePoints(sent.substring(common))
        val typed = new.substring(common)
        input.text("\b".repeat(erased) + typed)
        sent = new
    }

    /** Поле очистили без отправки на ПК (например, после Enter). */
    fun reset() {
        sent = ""
    }

    private fun codePoints(s: String) = s.count { !it.isLowSurrogate() }
}
