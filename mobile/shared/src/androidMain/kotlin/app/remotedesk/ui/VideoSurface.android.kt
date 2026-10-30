package app.remotedesk.ui

import android.graphics.SurfaceTexture
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import android.view.TextureView
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import app.remotedesk.core.AgentConnection
import app.remotedesk.core.VideoPacket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Видео экрана ПК: аппаратный декодер H.264 телефона рисует прямо в TextureView (её можно увеличивать и
 * сдвигать, как картинку). Декодер открывается заново при смене размера или сбое — и просит ключевой кадр.
 */
@Composable
internal actual fun VideoSurface(connection: AgentConnection, modifier: Modifier) {
    val player = remember(connection) { VideoPlayer(connection) }
    DisposableEffect(player) { onDispose { player.stop() } }
    AndroidView(factory = { context -> TextureView(context).apply { surfaceTextureListener = player } }, modifier)
}

private class VideoPlayer(private val connection: AgentConnection) : TextureView.SurfaceTextureListener {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var job: Job? = null
    private var surface: Surface? = null

    override fun onSurfaceTextureAvailable(texture: SurfaceTexture, width: Int, height: Int) {
        val target = Surface(texture)
        surface = target
        job = scope.launch { decode(target) }
    }

    override fun onSurfaceTextureSizeChanged(texture: SurfaceTexture, width: Int, height: Int) = Unit

    override fun onSurfaceTextureDestroyed(texture: SurfaceTexture): Boolean {
        job?.cancel()
        job = null
        surface?.release()
        surface = null
        return true
    }

    override fun onSurfaceTextureUpdated(texture: SurfaceTexture) = Unit

    fun stop() = scope.cancel()

    private suspend fun decode(target: Surface) {
        var decoder: Decoder? = null
        var askedKey = false
        // Декодер открывается только с ключевого кадра: до него картинку не собрать.
        connection.requestKeyFrame()
        try {
            for (packet in connection.video) {
                val current = decoder
                if (current == null || current.width != packet.width || current.height != packet.height || current.failed) {
                    current?.release()
                    decoder = null
                    if (!packet.key) {
                        if (!askedKey) connection.requestKeyFrame()
                        askedKey = true
                        continue
                    }
                    decoder = try {
                        Decoder(packet.width, packet.height, target)
                    } catch (_: Exception) {
                        null
                    } ?: continue
                }
                askedKey = false
                if (!decoder.feed(packet)) {
                    decoder.release()
                    decoder = null
                    connection.requestKeyFrame()
                    askedKey = true
                }
            }
        } finally {
            decoder?.release()
        }
    }
}

/** MediaCodec в режиме обратных вызовов: готовый кадр сразу выводится на экран, ничего не копится. */
private class Decoder(val width: Int, val height: Int, surface: Surface) {
    private val thread = HandlerThread("Видео ПК").apply { start() }
    private val inputs = Channel<Int>(Channel.UNLIMITED)
    private var time = 0L

    @Volatile
    var failed = false
        private set

    private val codec: MediaCodec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)

    init {
        try {
            codec.setCallback(
                object : MediaCodec.Callback() {
                    override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
                        inputs.trySend(index)
                    }

                    override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
                        runCatching { codec.releaseOutputBuffer(index, true) }
                    }

                    override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
                        failed = true
                    }

                    override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit
                },
                Handler(thread.looper),
            )
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
            if (Build.VERSION.SDK_INT >= 30) format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            format.setInteger(MediaFormat.KEY_PRIORITY, 0) // реальное время
            codec.configure(format, surface, null, 0)
            codec.start()
        } catch (e: Exception) {
            release()
            throw e
        }
    }

    /** false — декодер сломался, нужен новый (и ключевой кадр). */
    suspend fun feed(packet: VideoPacket): Boolean {
        if (failed) return false
        val index = withTimeoutOrNull(1_000) { inputs.receive() } ?: return false
        return try {
            val buffer = codec.getInputBuffer(index) ?: return false
            buffer.clear()
            buffer.put(packet.data)
            codec.queueInputBuffer(index, 0, packet.data.size, time, if (packet.key) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            time += 16_667
            true
        } catch (_: Exception) {
            false
        }
    }

    fun release() {
        runCatching { codec.stop() }
        runCatching { codec.release() }
        thread.quitSafely()
        inputs.close()
    }
}
