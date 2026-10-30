package app.remotedesk.core

import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
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
import kotlin.test.assertFalse
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
    private val shared = File(dataDir, "shared")
    private var agent: Process? = null

    private val available get() = exe.isFile && System.getProperty("os.name").startsWith("Windows")

    @BeforeTest
    fun startAgent() {
        if (!available) return
        val linkFile = File(dataDir, "link.txt")
        // Агенту открываем только временную папку теста, а не настоящие «Документы».
        val agentDir = File(dataDir, "agent").apply { mkdirs() }
        shared.mkdirs()
        File(shared, "hello.txt").writeText("Привет с ПК")
        val json = shared.path.replace("\\", "\\\\")
        File(agentDir, "shares.json").writeText(
            """{"folders":[{"id":"t","name":"Тест","path":"$json","writable":true}],"wholeComputer":false,"wholeComputerWritable":false,"inbox":"$json"}""",
        )
        launch(linkFile)
    }

    /** --input-log: агент записывает ввод в файл и не трогает настоящие мышь и клавиатуру. */
    private fun launch(linkFile: File, vararg extra: String) {
        agent = ProcessBuilder(
            exe.path, "--loopback", "--port", PORT.toString(),
            "--data-dir", File(dataDir, "agent").path, "--dev-link", linkFile.path,
            "--input-log", inputLog.path, *extra,
        ).redirectErrorStream(true).start()
        val deadline = System.currentTimeMillis() + 20_000
        while (!linkFile.isFile && System.currentTimeMillis() < deadline) {
            check(agent!!.isAlive) { "агент завершился с кодом ${agent!!.exitValue()}" }
            Thread.sleep(200)
        }
        check(linkFile.isFile) { "агент не записал ссылку за 20 с" }
    }

    private fun stop() {
        agent?.let {
            it.destroyForcibly()
            it.waitFor(5, TimeUnit.SECONDS)
        }
        agent = null
    }

    @Test
    fun sign_in_screen_agent_gives_screen_and_keys_only() {
        if (!available) {
            println("SKIPPED: агент не найден: $exe")
            return
        }
        runBlocking {
            withTimeout(60_000) {
                val paired = AgentConnection.pair(link(), device)
                assertTrue(!paired.connection.signIn)
                paired.connection.close()
                // Тот же ПК на экране входа Windows: помощник службы с той же папкой данных.
                stop()
                launch(File(dataDir, "link-signin.txt"), "--signin-test")
                val locked = AgentConnection.connect(paired.pc, device)
                assertTrue(locked.signIn)
                assertEquals(listOf("input", "power", "screen"), locked.permissions)
                locked.startScreen(null, 800)
                withTimeout(10_000) { locked.screenFrame.filterNotNull().first() }
                locked.close()
            }
        }
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
                // Таймер питания: агент сообщает о нём, отмена убирает. Агент с --input-log ПК не выключит.
                again.power.schedule(PowerAction.Shutdown, 600, force = true)
                val timer = withTimeout(5_000) { again.powerTimer.filterNotNull().first() }
                assertEquals(PowerAction.Shutdown, timer.action)
                assertTrue(timer.force)
                assertTrue(timer.remainingMs in 590_000..600_000, "осталось ${timer.remainingMs} мс")
                again.power.cancel()
                withTimeout(5_000) { again.powerTimer.first { it == null } }
                again.power.run(PowerAction.MonitorsOff, keepMonitorsOff = true)
                again.ping() // ping идёт после ввода: когда пришёл pong, ввод уже обработан
                again.close()
                val expected = listOf(
                    "move 1 -3", "wheel -60 0", "button right down", "button right up",
                    "key 0x11 down", "key 0x43 down", "key 0x43 up", "key 0x11 up",
                    "text Привет\b😀", "key 0x10 down", "power monitors_off keep", "key 0x10 up",
                )
                // Буфер обмена в тесте тоже записывается в журнал, а не в настоящий буфер ПК.
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

                // Файлы по HTTPS с тем же закреплённым сертификатом.
                val files = AgentFiles(paired.pc, device.id)
                try {
                    assertEquals(listOf("t"), files.roots().map { it.id })
                    assertEquals(listOf("hello.txt"), files.list("t", "").map { it.name })
                    val downloaded = java.io.ByteArrayOutputStream()
                    files.download("t", "hello.txt", 0) { b, n -> downloaded.write(b, 0, n) }
                    assertEquals("Привет с ПК", downloaded.toString(Charsets.UTF_8))

                    val data = "данные с телефона".encodeToByteArray()
                    val (name, have) = files.beginUpload("t", "", "hello.txt", Conflict.Rename)
                    assertEquals("hello (2).txt", name)
                    var pos = 0
                    files.upload("t", "", name, data.size.toLong(), have, { buf ->
                        if (pos >= data.size) -1 else data.copyInto(buf, 0, pos).let { val n = data.size - pos; pos = data.size; n }
                    }) {}
                    assertEquals("данные с телефона", File(shared, name).readText())

                    val err = assertFailsWith<FileError> { files.download("t", "../link.txt", 0) { _, _ -> } }
                    assertEquals("outside", err.code)
                } finally {
                    files.close()
                }

                val clip = AgentConnection.connect(paired.pc, device)
                clip.clipboardSet("из теста")
                assertEquals(PcClip("из теста", false), clip.clipboardGet())
                // Картинка — через HTTPS; после неё в буфере ПК нет текста, а есть картинка.
                val pictures = AgentFiles(paired.pc, device.id)
                pictures.putClipImage(ClipImage(byteArrayOf(1, 2, 3), "image/png"))
                assertEquals(PcClip("", true), clip.clipboardGet())
                assertEquals(listOf<Byte>(1, 2, 3), pictures.clipImage().toList())
                pictures.close()

                // «Отправить на телефон»: вторая копия агента с --send передаёт файл первой, та — телефону.
                val sent = File(dataDir, "с ПК.txt").apply { writeText("отправлено из Проводника") }
                ProcessBuilder(exe.path, "--data-dir", File(dataDir, "agent").path, "--send", sent.path).start().waitFor(10, TimeUnit.SECONDS)
                val offer = withTimeout(10_000) { clip.offers.receive() }.single()
                assertEquals("с ПК.txt", offer.name)
                val got = java.io.ByteArrayOutputStream()
                AgentFiles(paired.pc, device.id).run {
                    download(PcOffer.ROOT, offer.id, 0) { b, n -> got.write(b, 0, n) }
                    close()
                }
                assertEquals("отправлено из Проводника", got.toString(Charsets.UTF_8))

                // Экран: в режиме --input-log агент показывает ненастоящий экран 16×9, а касание пишет в журнал.
                clip.startScreen("test-2", 800)
                val shot = withTimeout(10_000) { clip.screenFrame.filterNotNull().first() }
                assertEquals(16 to 9, shot.width to shot.height)
                assertEquals("test-2", withTimeout(5_000) { clip.screenInfo.filterNotNull().first() }.monitor)
                clip.input.pointer(0.5f, 0.5f)
                clip.input.click()
                clip.ping()
                assertTrue(inputLog.readText().contains("moveto 2400 450"))
                clip.stopScreen()
                // Видео H.264 (тоже ненастоящее): первый кадр ключевой, курсор приходит отдельно.
                clip.startScreen(null, 800, video = true)
                val keyFrame = withTimeout(10_000) { clip.video.receive() }
                assertTrue(keyFrame.key)
                assertEquals(16 to 8, keyFrame.width to keyFrame.height)
                assertEquals(16 to 8, withTimeout(5_000) { clip.videoSize.filterNotNull().first() })
                withTimeout(5_000) { clip.screenInfo.first { it?.codec == "h264" } }
                val pointer = withTimeout(5_000) { clip.cursor.filterNotNull().first() }
                assertEquals(0.5f, pointer.x)
                assertTrue(pointer.visible)
                assertTrue(withTimeout(5_000) { clip.cursorImage.filterNotNull().first() }.png.isNotEmpty())
                clip.requestKeyFrame()
                withTimeout(5_000) { while (!clip.video.receive().key) Unit }
                clip.stopScreen()
                // Звук: у агента с --input-log он ненастоящий (тон), колонки ПК не трогаются.
                clip.startAudio(muteSpeakers = false)
                val sound = withTimeout(5_000) { List(5) { clip.audio.receive() } }
                assertTrue(sound.all { it.size in 10..1500 })
                clip.stopAudio()
                clip.close()

                val nobody = assertFailsWith<AgentException> { AgentConnection.connect(paired.pc.copy(port = PORT + 1), device) }
                assertEquals(Problem.Unreachable, nobody.problem)

                // Не из дома: домашний адрес молчит, отвечает адрес дома в интернете (здесь — тот же агент).
                val away = AgentConnection.connect(paired.pc.copy(hosts = listOf("10.255.255.1"), wakeHost = "127.0.0.1:9"), device)
                assertTrue(away.viaInternet)
                assertEquals("127.0.0.1", away.host)
                away.ping()
                away.close()
                // Дома побеждает домашний адрес, даже если адрес в интернете тоже указан.
                val home = AgentConnection.connect(paired.pc.copy(wakeHost = "localhost"), device)
                assertFalse(home.viaInternet)
                home.close()
            }
        }
    }

    private companion object {
        const val PORT = 47897
    }
}
