package app.remotedesk.core

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaFormat
import java.nio.ByteBuffer
import java.nio.ByteOrder

internal actual fun openOpusPlayer(): OpusPlayer? = try {
    AndroidOpusPlayer()
} catch (_: Exception) {
    null
}

/**
 * Opus расшифровывает встроенный декодер Android (MediaCodec), играет AudioTrack. Буфер проигрывателя —
 * 200 мс и пишется без ожидания: что не влезло, выбрасывается, поэтому звук не копит задержку.
 */
private class AndroidOpusPlayer : OpusPlayer {
    private val codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS)
    private val track: AudioTrack
    private val info = MediaCodec.BufferInfo()
    private var timeUs = 0L

    init {
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, RATE, 2).apply {
            setByteBuffer("csd-0", opusHead())
            setByteBuffer("csd-1", nanos(0))
            setByteBuffer("csd-2", nanos(80_000_000))
        }
        try {
            codec.configure(format, null, null, 0)
            codec.start()
        } catch (e: Exception) {
            codec.release()
            throw e
        }
        val minimum = AudioTrack.getMinBufferSize(RATE, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT)
        track = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build(),
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(RATE)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build(),
            )
            .setBufferSizeInBytes(maxOf(minimum, RATE * 4 / 5)) // 200 мс: 4 байта на кадр
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        track.play()
    }

    override fun play(packet: ByteArray) {
        val input = codec.dequeueInputBuffer(20_000)
        if (input < 0) return
        codec.getInputBuffer(input)?.apply {
            clear()
            put(packet)
        }
        codec.queueInputBuffer(input, 0, packet.size, timeUs, 0)
        timeUs += 20_000
        while (true) {
            val output = codec.dequeueOutputBuffer(info, 0)
            if (output == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) continue
            if (output < 0) break
            codec.getOutputBuffer(output)?.let { pcm ->
                pcm.position(info.offset)
                pcm.limit(info.offset + info.size)
                track.write(pcm, info.size, AudioTrack.WRITE_NON_BLOCKING)
            }
            codec.releaseOutputBuffer(output, false)
        }
    }

    override fun close() {
        runCatching { track.stop() }
        track.release()
        runCatching { codec.stop() }
        codec.release()
    }

    private companion object {
        const val RATE = 48_000

        /** Заголовок OpusHead (RFC 7845): версия 1, 2 канала, pre-skip 312, 48 кГц, без усиления, раскладка 0. */
        fun opusHead(): ByteBuffer = ByteBuffer.allocate(19).order(ByteOrder.LITTLE_ENDIAN).apply {
            put("OpusHead".encodeToByteArray())
            put(1)
            put(2)
            putShort(312)
            putInt(RATE)
            putShort(0)
            put(0)
            flip()
        }

        fun nanos(value: Long): ByteBuffer = ByteBuffer.allocate(8).order(ByteOrder.nativeOrder()).apply {
            putLong(value)
            flip()
        }
    }
}
