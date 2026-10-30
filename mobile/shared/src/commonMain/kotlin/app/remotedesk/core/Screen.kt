package app.remotedesk.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject

/** Монитор ПК. */
data class PcMonitor(val id: String, val name: String, val width: Int, val height: Int, val primary: Boolean)

/** Что показывает агент: все мониторы и id текущего. */
data class ScreenInfo(val monitors: List<PcMonitor>, val monitor: String) {
    val current: PcMonitor? get() = monitors.firstOrNull { it.id == monitor }

    companion object {
        internal fun parse(msg: JsonObject): ScreenInfo = ScreenInfo(
            (msg["monitors"] as? JsonArray).orEmpty().mapNotNull { e ->
                val o = e as? JsonObject ?: return@mapNotNull null
                PcMonitor(
                    o.str("id") ?: return@mapNotNull null,
                    o.str("name").orEmpty(),
                    (o.num("width") ?: 0).toInt(),
                    (o.num("height") ?: 0).toInt(),
                    o.bool("primary") ?: false,
                )
            },
            msg.str("monitor").orEmpty(),
        )
    }
}

/** Кадр экрана: номер, размер и JPEG. */
class ScreenImage(val number: Long, val width: Int, val height: Int, val jpeg: ByteArray) {
    companion object {
        /** Бинарный кадр WebSocket: [1][номер u32][ширина u16][высота u16][JPEG], числа big-endian. */
        fun parse(data: ByteArray): ScreenImage? {
            if (data.size < 10 || data[0].toInt() != 1) return null
            fun u(i: Int) = data[i].toLong() and 0xFF
            val number = (u(1) shl 24) or (u(2) shl 16) or (u(3) shl 8) or u(4)
            val width = ((u(5) shl 8) or u(6)).toInt()
            val height = ((u(7) shl 8) or u(8)).toInt()
            return ScreenImage(number, width, height, data.copyOfRange(9, data.size))
        }
    }
}
