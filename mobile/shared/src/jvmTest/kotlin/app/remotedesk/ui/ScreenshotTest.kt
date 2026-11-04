package app.remotedesk.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.ImageComposeScene
import androidx.compose.ui.unit.Density
import app.remotedesk.core.AgentConnection
import app.remotedesk.core.DeviceInfo
import app.remotedesk.core.DownloadTarget
import app.remotedesk.core.FoundPc
import app.remotedesk.core.LinkState
import app.remotedesk.core.LocalFile
import app.remotedesk.core.MacInfo
import app.remotedesk.core.PairingLink
import app.remotedesk.core.PhoneStorage
import app.remotedesk.core.PhoneClipboard
import app.remotedesk.core.ClipImage
import app.remotedesk.core.Problem
import app.remotedesk.core.SavedPc
import app.remotedesk.core.TouchpadSettings
import app.remotedesk.core.Transfers
import androidx.compose.material3.SnackbarHostState
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.jetbrains.skia.EncodedImageFormat
import java.io.File
import java.nio.file.Files
import java.util.concurrent.TimeUnit
import kotlin.test.Test

/**
 * Снимки экранов приложения в PNG — чтобы видеть интерфейс без телефона.
 * Запуск: gradlew :shared:jvmTest --tests app.remotedesk.ui.ScreenshotTest -Pshots=папка
 * Пульт подключается к настоящему агенту в режиме --loopback --input-log: агент не трогает
 * мышь и клавиатуру, а файлы показывает только из временной папки теста.
 */
class ScreenshotTest {
    private val out = System.getProperty("rdesk.shots").orEmpty().let(::File)
    private val exe = File(System.getProperty("rdesk.agentExe") ?: "")
    private val device = DeviceInfo("shots-phone", "Телефон", "jvm")

    @Test
    fun screens() {
        if (out.path.isEmpty()) {
            println("SKIPPED: нет -Pshots")
            return
        }
        out.mkdirs()
        val pc = SavedPc(
            id = "pc-1", name = "DESKTOP-ABC123", hosts = listOf("192.168.0.102"), port = 47800, fingerprint = "", token = "",
            macs = listOf(MacInfo("AA:BB:CC:DD:EE:FF", "Ethernet", wired = true)),
        )
        val laptop = pc.copy(id = "pc-2", name = "Ноутбук", hosts = listOf("192.168.0.105"))
        val found = listOf(FoundPc("pc-1", pc.name, pc.hosts, 47800, ""), FoundPc("pc-3", "OFFICE-PC", listOf("192.168.0.110"), 47800, ""))

        for (dark in listOf(false, true)) {
            val suffix = if (dark) "-dark" else ""
            shot("home-empty$suffix", dark) {
                HomeScreen(emptyList(), emptyList(), remember { SnackbarHostState() }, {}, emptyMap(), {}, {}, {}, {}, {})
            }
            shot("home$suffix", dark) {
                HomeScreen(listOf(pc, laptop), found, remember { SnackbarHostState() }, {}, emptyMap(), {}, {}, {}, {}, {})
            }
            shot("settings$suffix", dark) { SettingsScreen(TouchpadSettings(), {}, {}, {}) }
            shot("failed$suffix", dark) {
                PcScreen(LinkState.Failed("pc-1", pc.name, Problem.Unreachable, "Unreachable: 192.168.0.102"), pc, {}, {}, {}, {}, TouchpadSettings(), {}, {}, extras())
            }
        }
        withAgent { connection, paired ->
            for (dark in listOf(false, true)) {
                val suffix = if (dark) "-dark" else ""
                listOf("touchpad", "screen", "keyboard", "files", "power", "info").forEachIndexed { tab, name ->
                    shot("pc-$name$suffix", dark, frames = if (name in listOf("files", "info", "screen")) 40 else 8) {
                        PcScreen(LinkState.Connected(paired.id, connection, 16), paired, {}, {}, {}, {}, TouchpadSettings(), {}, {}, extras(), initialTab = tab)
                    }
                }
            }
        }
    }

    @Composable
    private fun extras(): RemoteExtras {
        val scope = rememberCoroutineScope()
        return remember {
            RemoteExtras(
                device.id, Transfers(scope, NoStorage), NoStorage, NoClipboard,
                listOf("Привет с телефона", "https://example.com/very/long/link/that/does/not/fit"), {}, { _, _ -> },
            )
        }
    }

    /** Экран 412×915 dp, как у обычного телефона, плотность 2.625 (1080×2402 точек). */
    private fun shot(name: String, dark: Boolean, frames: Int = 8, content: @Composable () -> Unit) {
        val density = 2.625f
        val scene = ImageComposeScene(1080, 2402, Density(density)) { RemoteDeskTheme(dark) { content() } }
        try {
            var time = 0L
            repeat(frames) {
                scene.render(time)
                time += 100_000_000L
                Thread.sleep(50)
            }
            val png = scene.render(time).encodeToData(EncodedImageFormat.PNG)!!.bytes
            File(out, "$name.png").writeBytes(png)
        } finally {
            scene.close()
        }
    }

    private fun withAgent(block: (AgentConnection, SavedPc) -> Unit) {
        if (!exe.isFile) {
            println("SKIPPED: агент не найден: $exe — снимков пульта не будет")
            return
        }
        val dataDir = Files.createTempDirectory("rdesk-shots").toFile()
        val shared = File(dataDir, "shared").apply { mkdirs() }
        File(shared, "Фото").mkdirs()
        File(shared, "Документы").mkdirs()
        File(shared, "отчёт.pdf").writeBytes(ByteArray(245_000))
        File(shared, "отпуск.jpg").writeBytes(ByteArray(3_400_000))
        File(shared, "заметки.txt").writeText("Привет с ПК")
        val agentDir = File(dataDir, "agent").apply { mkdirs() }
        val json = shared.path.replace("\\", "\\\\")
        File(agentDir, "shares.json").writeText(
            """{"folders":[{"id":"t","name":"Общая папка","path":"$json","writable":true}],"wholeComputer":false,"wholeComputerWritable":false,"inbox":"$json"}""",
        )
        val linkFile = File(dataDir, "link.txt")
        val agent = ProcessBuilder(
            exe.path, "--loopback", "--port", "47896", "--data-dir", agentDir.path,
            "--dev-link", linkFile.path, "--input-log", File(dataDir, "input.log").path, "--name", "DESKTOP-ABC123",
        ).redirectErrorStream(true).start()
        try {
            val deadline = System.currentTimeMillis() + 20_000
            while (!linkFile.isFile && System.currentTimeMillis() < deadline) Thread.sleep(200)
            check(linkFile.isFile) { "агент не записал ссылку" }
            val paired = runBlocking { withTimeout(30_000) { AgentConnection.pair(PairingLink.parse(linkFile.readText())!!, device) } }
            try {
                block(paired.connection, paired.pc)
            } finally {
                runBlocking { paired.connection.close() }
            }
        } finally {
            agent.destroyForcibly()
            agent.waitFor(5, TimeUnit.SECONDS)
            dataDir.deleteRecursively()
        }
    }

    private object NoClipboard : PhoneClipboard {
        override fun text(): String? = null
        override fun setText(text: String) = Unit
        override suspend fun image(): ClipImage? = null
        override suspend fun setImage(image: ClipImage) = Unit
    }

    private object NoStorage : PhoneStorage {
        override suspend fun createDownload(name: String, size: Long): DownloadTarget = error("не нужно для снимков")
        override suspend fun pickFiles(): List<LocalFile> = emptyList()
        override fun open(target: DownloadTarget) = Unit
    }
}
