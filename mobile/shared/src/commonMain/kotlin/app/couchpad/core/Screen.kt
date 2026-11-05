package app.couchpad.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject

/** Монитор ПК. */
data class PcMonitor(val id: String, val name: String, val width: Int, val height: Int, val primary: Boolean)

/** Что показывает агент: все мониторы и id текущего. */
data class ScreenInfo(val monitors: List<PcMonitor>, val monitor: String, val codec: String = "jpeg") {
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
            msg.str("codec") ?: "jpeg",
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

/** Кадр видео H.264 (Annex B): номер, размер видео и ключевой ли. */
class VideoPacket(val number: Long, val width: Int, val height: Int, val key: Boolean, val data: ByteArray) {
    companion object {
        /** Бинарный кадр WebSocket: [3][номер u32][ширина u16][высота u16][1 — ключевой][H.264], числа big-endian. */
        fun parse(data: ByteArray): VideoPacket? {
            if (data.size < 11 || data[0].toInt() != 3) return null
            fun u(i: Int) = data[i].toLong() and 0xFF
            return VideoPacket(
                (u(1) shl 24) or (u(2) shl 16) or (u(3) shl 8) or u(4),
                ((u(5) shl 8) or u(6)).toInt(),
                ((u(7) shl 8) or u(8)).toInt(),
                data[9].toInt() == 1,
                data.copyOfRange(10, data.size),
            )
        }
    }
}

/** Курсор ПК поверх видео: доли ширины и высоты монитора. */
data class RemoteCursor(val x: Float, val y: Float, val visible: Boolean)

/** Картинка курсора ПК: PNG, горячая точка и размер в пикселях монитора. */
class CursorImage(val png: ByteArray, val hotX: Int, val hotY: Int, val width: Int, val height: Int)

/** Есть ли на этой платформе декодер H.264 (на Android — всегда; JVM в тестах — нет, там картинки JPEG). */
internal expect val videoDecoderAvailable: Boolean
