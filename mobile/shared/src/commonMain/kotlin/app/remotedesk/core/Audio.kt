package app.remotedesk.core

/** Проигрыватель звука ПК: куски Opus по 20 мс, 48 кГц стерео. */
interface OpusPlayer {
    /** Пакет Opus без заголовка кадра. Если телефон не успевает, лишнее выбрасывается — звук не отстаёт. */
    fun play(packet: ByteArray)

    fun close()
}

/** null — на этой платформе звук не играет (JVM в тестах). */
internal expect fun openOpusPlayer(): OpusPlayer?

/** Бинарный кадр звука: [2][номер u32 big-endian][Opus]. */
internal object AudioFrame {
    const val TYPE = 2

    fun opus(data: ByteArray): ByteArray? =
        if (data.size > 5 && data[0].toInt() == TYPE) data.copyOfRange(5, data.size) else null
}
