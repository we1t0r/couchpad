package app.remotedesk.core

import com.russhwolf.settings.Settings
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.serializer

enum class ButtonSize(val heightDp: Int) { Small(56), Medium(72), Large(96) }

/** Настройки пульта. Значения по умолчанию подобраны так, чтобы трогать ничего не пришлось. */
@Serializable
data class TouchpadSettings(
    /** Множитель скорости курсора, 0.3–3. */
    val sensitivity: Float = 1f,
    /** Насколько быстрое движение пальца ускоряет курсор, 0–1. */
    val acceleration: Float = 0.5f,
    /** Скорость прокрутки, 0.3–3. */
    val scrollSpeed: Float = 1f,
    /** Обратная прокрутка: палец вверх — страница вверх (как колесо мыши, а не как тачпад ноутбука). */
    val invertScroll: Boolean = false,
    val tapToClick: Boolean = true,
    /** Двойное касание с удержанием — перетаскивание. Одиночный тап тогда срабатывает с задержкой ~0,25 с. */
    val tapAndDrag: Boolean = true,
    val vibration: Boolean = true,
    val buttonSize: ButtonSize = ButtonSize.Medium,
    val middleButton: Boolean = false,
    /** Для левой руки: кнопки и полоса прокрутки зеркально. */
    val leftHanded: Boolean = false,
    val scrollStrip: Boolean = true,
    val keepScreenOn: Boolean = true,
    /** Спрашивать перед выключением, перезагрузкой, сном и выходом из учётной записи. */
    val confirmPower: Boolean = true,
    /** Погашенные мониторы будит только кнопка в приложении (или клавиша на ПК), а не мышь. */
    val monitorsWakeByAppOnly: Boolean = false,
    /** Звук ПК на вкладке «Экран» (кнопка с динамиком). */
    val screenSound: Boolean = false,
    /** Пока звук идёт на телефон, колонки ПК молчат. */
    val mutePcSpeakers: Boolean = true,
)

class RemoteSettings(private val settings: Settings) {
    private val _touchpad = MutableStateFlow(load())
    val touchpad: StateFlow<TouchpadSettings> = _touchpad.asStateFlow()

    fun update(transform: (TouchpadSettings) -> TouchpadSettings) {
        val value = transform(_touchpad.value)
        _touchpad.value = value
        settings.putString(KEY, ProtocolJson.encodeToString(TouchpadSettings.serializer(), value))
    }

    fun reset() = update { TouchpadSettings() }

    private val _clipHistory = MutableStateFlow(loadHistory())

    /** Последние 20 текстов, которые ходили через буфер обмена (новые сверху). */
    val clipHistory: StateFlow<List<String>> = _clipHistory.asStateFlow()

    fun addClip(text: String) {
        if (text.isBlank()) return
        val list = (listOf(text) + _clipHistory.value.filter { it != text }).take(MAX_HISTORY)
        _clipHistory.value = list
        settings.putString(KEY_HISTORY, ProtocolJson.encodeToString(ListSerializer(String.serializer()), list))
    }

    fun clearClips() {
        _clipHistory.value = emptyList()
        settings.remove(KEY_HISTORY)
    }

    private fun loadHistory(): List<String> = try {
        settings.getStringOrNull(KEY_HISTORY)?.let { ProtocolJson.decodeFromString(ListSerializer(String.serializer()), it) }
    } catch (_: Exception) {
        null
    }.orEmpty()

    private fun load(): TouchpadSettings = try {
        settings.getStringOrNull(KEY)?.let { ProtocolJson.decodeFromString(TouchpadSettings.serializer(), it) }
    } catch (_: Exception) {
        null
    } ?: TouchpadSettings()

    private companion object {
        const val KEY = "touchpad"
        const val KEY_HISTORY = "clipHistory"
        const val MAX_HISTORY = 20
    }
}
