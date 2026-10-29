package app.remotedesk.core

import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import java.io.File
import java.nio.file.Files
import java.util.concurrent.TimeUnit
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * Настоящий агент для Windows против настоящего клиента. Агент запускается с --loopback:
 * слушает только 127.0.0.1, в сеть не объявляется, брандмауэр не спрашивает.
 * Если агент не собран (или это не Windows) — тест пропускается.
 */
class AgentInteropTest {
    private val exe = File(System.getProperty("rdesk.agentExe") ?: "")
    private val dataDir = Files.createTempDirectory("rdesk-interop").toFile()
    private val device = DeviceInfo("interop-phone", "Тест JVM", "jvm")
    private val inputLog = File(dataDir, "input.log")
    private var agent: Process? = null

    private val available get() = exe.isFile && System.getProperty("os.name").startsWith("Windows")

    @BeforeTest
    fun startAgent() {
        if (!available) return
        val linkFile = File(dataDir, "link.txt")
        // --input-log: агент записывает ввод в файл и не трогает настоящие мышь и клавиатуру.
        agent = ProcessBuilder(
            exe.path, "--loopback", "--port", PORT.toString(),
            "--data-dir", File(dataDir, "agent").path, "--dev-link", linkFile.path,
            "--input-log", inputLog.path,
        ).redirectErrorStream(true).start()
        val deadline = System.currentTimeMillis() + 20_000
        while (!linkFile.isFile && System.currentTimeMillis() < deadline) {
            check(agent!!.isAlive) { "агент завершился с кодом ${agent!!.exitValue()}" }
            Thread.sleep(200)
        }
        check(linkFile.isFile) { "агент не записал ссылку за 20 с" }
    }

    @AfterTest
    fun stopAgent() {
        agent?.let {
            it.destroyForcibly()
            it.waitFor(5, TimeUnit.SECONDS)
        }
        dataDir.deleteRecursively()
    }

    private fun link() = PairingLink.parse(File(dataDir, "link.txt").readText())!!

    @Test
    fun pair_ping_reconnect_and_reject_wrong_pc() {
        if (!available) {
            println("SKIPPED: агент не найден: $exe")
            return
        }
        runBlocking {
            withTimeout(60_000) {
                val link = link()
                assertEquals(listOf("127.0.0.1"), link.hosts)

                val paired = AgentConnection.pair(link, device)
                assertEquals(link.agentId, paired.pc.id)
                assertTrue(paired.pc.token.isNotEmpty())
                val first = paired.connection.ping()
                println("pair ok, ping $first ms, agent ${paired.connection.agent}")
                paired.connection.close()

                // Тот же ключ второй раз не работает.
                val reused = assertFailsWith<AgentException> { AgentConnection.pair(link, device.copy(id = "other")) }
                assertEquals(Problem.BadCode, reused.problem)

                val again = AgentConnection.connect(paired.pc, device)
                assertTrue(again.permissions.contains("input"))
                repeat(3) { again.ping() }

                // Ввод доходит до агента в том же порядке, а зажатое отпускается при отключении.
                with(again.input) {
                    move(0.6f, 0f)
                    move(0.6f, -3f)
                    scroll(-0.5f)
                    click(MouseButton.Right)
                    keys("ctrl", "c")
                    text("Привет\b😀\n")
                    key("shift", true)
                }
                again.ping() // ping идёт после ввода: когда пришёл pong, ввод уже обработан
                again.close()
                val expected = listOf(
                    "move 1 -3", "wheel -60 0", "button right down", "button right up",
                    "key 0x11 down", "key 0x43 down", "key 0x43 up", "key 0x11 up",
                    "text Привет\b😀", "key 0x10 down", "key 0x10 up",
                )
                var lines = emptyList<String>()
                for (attempt in 1..50) {
                    lines = if (inputLog.isFile) inputLog.readText().split("\n").filter { it.isNotEmpty() } else emptyList()
                    if (lines.size >= expected.size) break
                    Thread.sleep(100)
                }
                assertEquals(expected, lines)

                val wrongToken = assertFailsWith<AgentException> { AgentConnection.connect(paired.pc.copy(token = "nope"), device) }
                assertEquals(Problem.Revoked, wrongToken.problem)

                val wrongPc = assertFailsWith<AgentException> {
                    AgentConnection.connect(paired.pc.copy(fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), device)
                }
                assertEquals(Problem.WrongPc, wrongPc.problem)

                val nobody = assertFailsWith<AgentException> { AgentConnection.connect(paired.pc.copy(port = PORT + 1), device) }
                assertEquals(Problem.Unreachable, nobody.problem)
            }
        }
    }

    private companion object {
        const val PORT = 47897
    }
}
