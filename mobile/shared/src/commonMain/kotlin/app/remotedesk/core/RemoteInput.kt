package app.remotedesk.core

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.getAndUpdate
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonPrimitive

enum class MouseButton(internal val wire: String) { Left("left"), Right("right"), Middle("middle") }

/**
 * Ввод на ПК (см. «Ввод» в protocol/PROTOCOL.md). Отправка не ждёт ответа.
 *
 * Движения складываются: пока порция движения ждёт в очереди, новое движение прибавляется к ней.
 * Поэтому очередь не растёт, даже если сеть на миг притормозила, и курсор не «догоняет» палец.
 * Прибавлять можно только к последнему, что стоит в очереди: любое другое сообщение «закрывает»
 * порцию, и порядок событий не нарушается.
 *
 * В очередь ([sink]) попадают строки и порции [Batch]; отправитель превращает элемент в текст
 * через [resolve] в последний момент перед отправкой. Вызывать методы ввода нужно из одного потока
 * (интерфейса); [resolve] — из потока отправки.
 */
class RemoteInput(private val sink: (Any) -> Unit) {
    /** Порция движения или прокрутки в очереди. null в [amount] — порцию уже забрали на отправку. */
    private class Batch(val scroll: Boolean, dx: Float, dy: Float) {
        val amount = MutableStateFlow<Pair<Float, Float>?>(dx to dy)

        /** false — порцию уже забрали, нужна новая. */
        fun add(dx: Float, dy: Float): Boolean {
            while (true) {
                val current = amount.value ?: return false
                if (amount.compareAndSet(current, (current.first + dx) to (current.second + dy))) return true
            }
        }
    }

    /** Порция в конце очереди, к которой ещё можно прибавлять. */
    private var open: Batch? = null

    private fun send(text: String) {
        open = null
        sink(text)
    }

    private fun accumulate(scroll: Boolean, dx: Float, dy: Float) {
        if (dx == 0f && dy == 0f) return
        val batch = open
        if (batch != null && batch.scroll == scroll && batch.add(dx, dy)) return
        open = Batch(scroll, dx, dy).also(sink)
    }

    /** Текст сообщения для отправки; null — порция пуста. */
    fun resolve(item: Any): String? = when (item) {
        is Batch -> item.amount.getAndUpdate { null }?.let { (dx, dy) ->
            if (item.scroll) message("scroll", "dy" to dy.round(), "dx" to dx.round())
            else message("move", "dx" to dx.round(), "dy" to dy.round())
        }
        else -> item as String
    }

    fun move(dx: Float, dy: Float) = accumulate(scroll = false, dx, dy)

    fun button(button: MouseButton, down: Boolean) = send(message("button", "b" to button.wire, "down" to down))

    fun click(button: MouseButton = MouseButton.Left, count: Int = 1) =
        send(message("click", "b" to button.wire, "n" to count))

    /** В щелчках колеса: [dy] > 0 — вверх, [dx] > 0 — вправо. */
    fun scroll(dy: Float, dx: Float = 0f) {
        accumulate(scroll = true, dx, dy)
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
