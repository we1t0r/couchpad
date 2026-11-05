package app.couchpad.core

import kotlin.math.min
import kotlin.math.sqrt

/** Расчёты тачпада, отдельно от Compose — чтобы их можно было проверить тестами. */
object PointerMath {
    /** Пикселей ПК на 1 dp движения пальца при чувствительности 1 и медленном движении. */
    const val BASE_GAIN = 1.4f

    /** dp движения пальца на один щелчок колеса при скорости прокрутки 1. */
    const val DP_PER_NOTCH = 36f

    /** dp горизонтального движения тремя пальцами на одно переключение окна. */
    const val DP_PER_WINDOW = 70f

    /**
     * Сдвиг курсора (пиксели ПК) для сдвига пальца [dxDp], [dyDp] за [dtMs].
     * Чем быстрее палец, тем больше множитель: медленно — точно, быстро — через весь экран.
     */
    fun cursor(dxDp: Float, dyDp: Float, dtMs: Long, settings: TouchpadSettings): Pair<Float, Float> {
        val distance = sqrt(dxDp * dxDp + dyDp * dyDp)
        val speed = if (dtMs > 0) distance / dtMs else 0f // dp/мс
        val boost = 1f + settings.acceleration * 2.5f * min(speed, 3f)
        val gain = BASE_GAIN * settings.sensitivity * boost
        return dxDp * gain to dyDp * gain
    }

    /**
     * Щелчки колеса для сдвига двух пальцев. По умолчанию — как тачпад ноутбука:
     * палец вверх — содержимое уезжает вверх (то есть прокрутка вниз).
     */
    fun scroll(dxDp: Float, dyDp: Float, settings: TouchpadSettings): Pair<Float, Float> {
        val sign = if (settings.invertScroll) -1f else 1f
        val k = settings.scrollSpeed / DP_PER_NOTCH
        return -dxDp * k * sign to dyDp * k * sign
    }
}
