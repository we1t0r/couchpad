package app.remotedesk.core

import com.russhwolf.settings.Settings
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable

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

    private fun load(): TouchpadSettings = try {
        settings.getStringOrNull(KEY)?.let { ProtocolJson.decodeFromString(TouchpadSettings.serializer(), it) }
    } catch (_: Exception) {
        null
    } ?: TouchpadSettings()

    private companion object {
        const val KEY = "touchpad"
    }
}
