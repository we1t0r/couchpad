package app.remotedesk.core

import com.russhwolf.settings.MapSettings
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class RemoteInputTest {
    private val sent = mutableListOf<String>()
    private val input = RemoteInput { text, _ -> sent += text }

    private fun texts() = sent.map { parseMessage(it)!!.str("s") }

    @Test
    fun relay_types_appended_text_and_backspaces_edits() {
        val relay = TextRelay(input)
        relay.onChange("При")
        relay.onChange("Привет")
        relay.onChange("Прив") // стёрли два символа
        relay.onChange("Привет!") // автозамена переписала хвост
        relay.onChange("Привет! 😀")
        relay.onChange("Привет! ") // эмодзи стирается одним Backspace

        assertEquals(listOf("При", "вет", "\b\b", "ет!", " 😀", "\b"), texts())
    }

    @Test
    fun relay_reset_starts_over() {
        val relay = TextRelay(input)
        relay.onChange("abc")
        relay.reset()
        relay.onChange("x")
        assertEquals(listOf("abc", "x"), texts())
    }

    @Test
    fun messages_have_protocol_shape() {
        input.move(1.23456f, -2f)
        input.keys("ctrl", "c")
        input.button(MouseButton.Right, true)
        input.move(0f, 0f) // пустое движение не отправляется

        assertEquals(3, sent.size)
        val move = parseMessage(sent[0])!!
        assertEquals("move", move.type)
        assertEquals("1.235", move["dx"].toString())
        assertEquals("""{"t":"keys","k":["ctrl","c"]}""", sent[1])
        assertEquals("""{"t":"button","b":"right","down":true}""", sent[2])
    }

    @Test
    fun acceleration_makes_fast_moves_longer() {
        val s = TouchpadSettings()
        val (slow, _) = PointerMath.cursor(10f, 0f, 100, s)
        val (fast, _) = PointerMath.cursor(10f, 0f, 5, s)
        assertTrue(fast > slow * 1.5f)
        val (flat, _) = PointerMath.cursor(10f, 0f, 5, s.copy(acceleration = 0f))
        assertEquals(10f * PointerMath.BASE_GAIN, flat, 0.001f)
    }

    @Test
    fun scroll_direction_follows_fingers_unless_inverted() {
        val s = TouchpadSettings()
        // Пальцы вверх — как на тачпаде ноутбука: колесо «вниз».
        assertTrue(PointerMath.scroll(0f, -PointerMath.DP_PER_NOTCH, s).second < 0)
        assertEquals(1f, PointerMath.scroll(0f, -PointerMath.DP_PER_NOTCH, s.copy(invertScroll = true)).second, 0.001f)
    }

    @Test
    fun settings_survive_restart_and_reset() {
        val storage = MapSettings()
        RemoteSettings(storage).update { it.copy(sensitivity = 2f, leftHanded = true) }
        val reloaded = RemoteSettings(storage)
        assertEquals(2f, reloaded.touchpad.value.sensitivity)
        assertTrue(reloaded.touchpad.value.leftHanded)
        reloaded.reset()
        assertEquals(TouchpadSettings(), RemoteSettings(storage).touchpad.value)
    }
}
