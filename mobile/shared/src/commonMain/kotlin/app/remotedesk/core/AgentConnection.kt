package app.remotedesk.core

import io.ktor.client.HttpClient
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import io.ktor.client.plugins.websocket.webSocketSession
import io.ktor.websocket.Frame
import io.ktor.websocket.close
import io.ktor.websocket.readText
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.onSubscription
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlin.time.TimeSource

/** Что пошло не так — для понятного пользователю объяснения. */
enum class Problem {
    /** Компьютер не ответил ни по одному адресу. */
    Unreachable,

    /** Ответил компьютер с другим сертификатом: не тот ПК или RemoteDesk переустановлен. */
    WrongPc,
    BadCode,
    CodeExpired,
    TooManyAttempts,

    /** Телефон удалили из списка на компьютере. */
    Revoked,
    Timeout,
    Incompatible,
    Protocol,

    /** Компьютер закрыл соединение (выключили программу, сон и т. п.). */
    Closed,
}

class AgentException(val problem: Problem, detail: String? = null, cause: Throwable? = null) :
    Exception(detail ?: problem.name, cause)

internal fun problemForCode(code: String?): Problem = when (code) {
    Protocol.Errors.BAD_SECRET -> Problem.BadCode
    Protocol.Errors.SECRET_EXPIRED -> Problem.CodeExpired
    Protocol.Errors.TOO_MANY_ATTEMPTS -> Problem.TooManyAttempts
    Protocol.Errors.REVOKED, Protocol.Errors.NOT_AUTHENTICATED -> Problem.Revoked
    Protocol.Errors.UNSUPPORTED_VERSION -> Problem.Incompatible
    else -> Problem.Protocol
}

/**
 * Клиент для HTTPS/WSS, который доверяет только сертификату с этим отпечатком.
 * [onMismatch] вызывается, если сертификат оказался чужим.
 */
internal expect fun pinnedHttpClient(fingerprint: String, onMismatch: () -> Unit): HttpClient

/** Установленное и проверенное соединение с агентом. */
class AgentConnection private constructor(
    private val client: HttpClient,
    private val session: DefaultClientWebSocketSession,
    val host: String,
    val agent: AgentInfo,
    val permissions: List<String>,
    /** ПК на экране входа Windows: это помощник службы входа, после входа его сменит обычный агент. */
    val signIn: Boolean = false,
    /** Подключились не из дома, по адресу дома в интернете: экран идёт в экономном качестве. */
    val viaInternet: Boolean = false,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val pongs = MutableSharedFlow<Long>(extraBufferCapacity = 16)
    private var nextPing = 1L
    private val _closed = MutableStateFlow<AgentException?>(null)

    /** null, пока соединение живо; иначе — почему закрылось. */
    val closed: StateFlow<AgentException?> = _closed.asStateFlow()

    // Ввод идёт через очередь: интерфейс не ждёт сети. Движения в ней складываются (см. RemoteInput),
    // поэтому очередь не разрастается, а нажатия и отпускания не теряются никогда.
    private val outbox = Channel<Any>(Channel.UNLIMITED)

    /** Мышь и клавиатура ПК. */
    val input = RemoteInput(::enqueue)

    /** Питание и мониторы ПК. */
    val power = RemotePower(::enqueue)

    private val _powerTimer = MutableStateFlow<PowerTimer?>(null)

    /** Таймер питания на ПК (общий для всех телефонов). */
    val powerTimer: StateFlow<PowerTimer?> = _powerTimer.asStateFlow()

    private val _wolStatus = MutableStateFlow<WolStatus?>(null)

    /** Готов ли ПК к включению по сети (ответ на [checkWol]). */
    val wolStatus: StateFlow<WolStatus?> = _wolStatus.asStateFlow()

    /** Спросить агента о настройках включения по сети; [openWizard] — ещё и открыть мастер на экране ПК. */
    fun checkWol(openWizard: Boolean = false) = enqueue(message(if (openWizard) "wol_setup" else "wol_check"))

    private val clipReplies = MutableSharedFlow<JsonObject>(extraBufferCapacity = 4)

    /** Что в буфере обмена ПК: текст или признак картинки. */
    suspend fun clipboardGet(): PcClip = withTimeout(10_000) {
        val reply = clipReplies.onSubscription { enqueue(message("clip_get")) }.first { it.type == "clip" }
        PcClip(reply.str("text").orEmpty(), reply.bool("image") ?: false)
    }

    /** Положить текст в буфер обмена ПК. */
    suspend fun clipboardSet(text: String) {
        withTimeout(10_000) {
            clipReplies.onSubscription { enqueue(message("clip_set", "text" to text)) }.first { it.type == "clip_done" }
        }
    }

    private val _offers = Channel<List<PcOffer>>(Channel.UNLIMITED)

    /** Файлы, которые компьютер прислал сам («Отправить на телефон» в Проводнике). Читает один получатель. */
    val offers: ReceiveChannel<List<PcOffer>> = _offers

    private val _phoneFileRequests = Channel<JsonObject>(Channel.UNLIMITED)

    /** Запросы компьютера к папкам телефона ({"t":"pf"}); отвечать — [replyPhoneFiles]. */
    val phoneFileRequests: ReceiveChannel<JsonObject> = _phoneFileRequests

    /** Ответ на запрос [id]: поля [fields] (или «error» с текстом для человека за компьютером). */
    fun replyPhoneFiles(id: Long, fields: JsonObject) {
        enqueue(JsonObject(fields + mapOf("t" to JsonPrimitive("pf_r"), "id" to JsonPrimitive(id))).toString())
    }

    private val _screenInfo = MutableStateFlow<ScreenInfo?>(null)

    /** Мониторы ПК и какой сейчас показывается (после [startScreen]). */
    val screenInfo: StateFlow<ScreenInfo?> = _screenInfo.asStateFlow()

    private val _screenFrame = MutableStateFlow<ScreenImage?>(null)

    /** Последний кадр экрана. Старые не копятся: интерфейс рисует только свежий. */
    val screenFrame: StateFlow<ScreenImage?> = _screenFrame.asStateFlow()

    /** Показывать экран ПК: [monitor] — id монитора (null — основной), [maxWidth] — ширина кадра в пикселях. */
    fun startScreen(monitor: String?, maxWidth: Int) = enqueue(
        // Через интернет — меньше и сжатее: мобильный интернет и отдача домашнего роутера обычно не выдержат 30 кадров Full HD.
        if (viaInternet) message("screen_start", "monitor" to monitor, "maxWidth" to minOf(maxWidth, 1280), "quality" to 50, "fps" to 15)
        else message("screen_start", "monitor" to monitor, "maxWidth" to maxWidth, "quality" to 70, "fps" to 30),
    )

    fun stopScreen() {
        enqueue(message("screen_stop"))
        _screenFrame.value = null
    }

    private fun onBinary(data: ByteArray) {
        val image = ScreenImage.parse(data) ?: return
        // Подтверждаем сразу: агент держит в пути не больше двух кадров, поэтому задержка не копится.
        enqueue(message("screen_ack", "n" to image.number))
        _screenFrame.value = image
    }

    private val _notices = MutableSharedFlow<String>(extraBufferCapacity = 8)

    /** Ошибки агента, о которых стоит сказать пользователю (например, «гибернация выключена»). */
    val notices: SharedFlow<String> = _notices

    private fun enqueue(item: Any) {
        outbox.trySend(item)
    }

    init {
        scope.launch { readLoop() }
        scope.launch {
            for (item in outbox) {
                val text = input.resolve(item) ?: continue
                try {
                    session.send(Frame.Text(text))
                } catch (e: CancellationException) {
                    throw e
                } catch (_: Exception) {
                    break
                }
            }
        }
    }

    private suspend fun readLoop() {
        var reason = AgentException(Problem.Closed)
        try {
            for (frame in session.incoming) {
                if (frame is Frame.Binary) {
                    onBinary(frame.data)
                    continue
                }
                val msg = (frame as? Frame.Text)?.let { parseMessage(it.readText()) } ?: continue
                when (msg.type) {
                    "pong" -> msg.num("n")?.let { pongs.emit(it) }
                    "power_timer" -> _powerTimer.value = RemotePower.parseTimer(msg)
                    "wol_status" -> _wolStatus.value = WolStatus.parse(msg)
                    "clip", "clip_done" -> clipReplies.emit(msg)
                    "pf" -> _phoneFileRequests.trySend(msg)
                    "screen_info" -> _screenInfo.value = ScreenInfo.parse(msg)
                    "offer" -> PcOffer.parse(msg).takeIf { it.isNotEmpty() }?.let { _offers.trySend(it) }
                    "error" -> {
                        val problem = problemForCode(msg.str("code"))
                        // Ошибка, после которой агент закрывает соединение, — запоминаем её как причину.
                        if (problem == Problem.Revoked || problem == Problem.Incompatible)
                            reason = AgentException(problem, msg.str("msg"))
                        else
                            _notices.tryEmit(msg.str("msg") ?: msg.str("code").orEmpty())
                    }
                    "bye" -> reason = AgentException(Problem.Closed, msg.str("reason"))
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            reason = AgentException(Problem.Closed, e.message, e)
        } finally {
            _closed.compareAndSet(null, reason)
            client.close()
        }
    }

    /** Задержка туда-обратно в миллисекундах. */
    suspend fun ping(timeoutMs: Long = 5_000): Long {
        closed.value?.let { throw it }
        val n = nextPing++
        val started = TimeSource.Monotonic.markNow()
        try {
            withTimeout(timeoutMs) {
                // Через общую очередь: pong означает, что весь ввод до него агент уже обработал.
                pongs.onSubscription { enqueue(message("ping", "n" to n)) }.first { it == n }
            }
        } catch (e: TimeoutCancellationException) {
            throw AgentException(Problem.Timeout, cause = e)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            throw closed.value ?: AgentException(Problem.Closed, e.message, e)
        }
        return started.elapsedNow().inWholeMilliseconds
    }

    suspend fun close() {
        withContext(NonCancellable) {
            _closed.compareAndSet(null, AgentException(Problem.Closed, "closed by app"))
            try {
                withTimeout(1_000) {
                    session.send(Frame.Text(message("bye")))
                    session.close()
                }
            } catch (_: Exception) {
            }
            scope.cancel()
            client.close()
        }
    }

    /** Результат сопряжения: что сохранить и открытое соединение. */
    class Paired(val pc: SavedPc, val connection: AgentConnection)

    companion object {
        private const val CONNECT_TIMEOUT_MS = 4_000L
        private const val INTERNET_TIMEOUT_MS = 8_000L
        private const val INTERNET_DELAY_MS = 1_500L

        /** Сопряжение по ссылке из QR-кода. */
        suspend fun pair(link: PairingLink, device: DeviceInfo): Paired {
            if (link.isNewerProtocol) throw AgentException(Problem.Incompatible)
            val hello = message(
                "pair",
                "secret" to link.secret,
                "deviceId" to device.id,
                "deviceName" to device.name,
                "platform" to device.platform,
                "appVersion" to Protocol.APP_VERSION,
            )
            val opened = open(link.hosts, null, link.port, link.fingerprint, hello, expected = "paired")
            val token = opened.reply.str("token")
            val agent = opened.reply.agentInfo()
            if (token.isNullOrEmpty() || agent == null || agent.id != link.agentId) {
                opened.dispose()
                throw AgentException(Problem.Protocol, "bad paired reply")
            }
            val connection = AgentConnection(opened.client, opened.session, opened.host, agent, emptyList())
            // Адрес, который сработал, ставим первым — с него начнём в следующий раз.
            val hosts = listOf(opened.host) + link.hosts.filter { it != opened.host }
            val pc = SavedPc(
                id = agent.id,
                name = agent.name,
                hosts = hosts,
                port = link.port,
                fingerprint = link.fingerprint,
                token = token,
                os = agent.os,
                agentVersion = agent.version,
                macs = agent.macs,
            )
            return Paired(pc, connection)
        }

        /** Подключение уже сопряжённого компьютера. */
        suspend fun connect(pc: SavedPc, device: DeviceInfo): AgentConnection {
            val hello = message(
                "hello",
                "deviceId" to device.id,
                "token" to pc.token,
                "appVersion" to Protocol.APP_VERSION,
            )
            val opened = open(pc.hosts, pc.internetHost, pc.port, pc.fingerprint, hello, expected = "welcome")
            val agent = opened.reply.agentInfo()
            if (agent == null || agent.id != pc.id) {
                opened.dispose()
                throw AgentException(Problem.WrongPc)
            }
            return AgentConnection(
                opened.client, opened.session, opened.host, agent, opened.reply.strings("permissions"),
                signIn = opened.reply.bool("signin") ?: false,
                viaInternet = opened.viaInternet,
            )
        }

        private class Opened(
            val client: HttpClient,
            val session: DefaultClientWebSocketSession,
            val host: String,
            val reply: JsonObject,
            val viaInternet: Boolean,
        ) {
            suspend fun dispose() {
                try {
                    session.close()
                } catch (_: Exception) {
                }
                client.close()
            }
        }

        /** Ошибка связи с одним адресом: другие адреса ещё могут ответить. */
        private class HostFailed(val reason: AgentException) : Exception(reason)

        /**
         * Пробует домашние адреса [hosts] разом, а чуть позже — адрес дома в интернете [internet]: дома
         * ответит локальный адрес, и картинка пойдёт по Wi-Fi, а не через роутер. Первый ответивший побеждает.
         * Ответ агента с ошибкой — окончательный, другие адреса уже не ждём.
         */
        private suspend fun open(
            hosts: List<String>,
            internet: String?,
            port: Int,
            fingerprint: String,
            first: String,
            expected: String,
        ): Opened = coroutineScope {
            val local = hosts.distinct()
            val remote = internet?.takeIf { it !in local }
            val winner = CompletableDeferred<Opened>()
            val failure = MutableStateFlow(AgentException(Problem.Unreachable))
            val attempts = (local.map { it to false } + listOfNotNull(remote?.let { it to true })).map { (host, viaInternet) ->
                launch {
                    if (viaInternet && local.isNotEmpty()) delay(INTERNET_DELAY_MS)
                    try {
                        val opened = attempt(host, viaInternet, port, fingerprint, first, expected)
                        if (!winner.complete(opened)) withContext(NonCancellable) { opened.dispose() }
                    } catch (e: HostFailed) {
                        // Чужой сертификат важнее «не ответил», молчание после подключения — важнее отказа сети.
                        failure.update { old -> if (rank(e.reason.problem) >= rank(old.problem)) e.reason else old }
                    } catch (e: AgentException) {
                        winner.completeExceptionally(e)
                    }
                }
            }
            launch {
                attempts.joinAll()
                winner.completeExceptionally(failure.value)
            }
            try {
                winner.await()
            } finally {
                attempts.forEach { it.cancel() }
            }
        }

        private fun rank(problem: Problem) = when (problem) {
            Problem.WrongPc -> 2
            Problem.Timeout -> 1
            else -> 0
        }

        private suspend fun attempt(
            host: String,
            viaInternet: Boolean,
            port: Int,
            fingerprint: String,
            first: String,
            expected: String,
        ): Opened {
            val timeout = if (viaInternet) INTERNET_TIMEOUT_MS else CONNECT_TIMEOUT_MS
            val mismatch = MutableStateFlow(false)
            val client = pinnedHttpClient(fingerprint) { mismatch.value = true }
            var session: DefaultClientWebSocketSession? = null
            try {
                session = withTimeout(timeout) { client.webSocketSession("wss://$host:$port/ws") }
                session.send(Frame.Text(first))
                val frame = withTimeout(timeout) { session.incoming.receive() }
                val reply = (frame as? Frame.Text)?.let { parseMessage(it.readText()) }
                    ?: throw AgentException(Problem.Protocol, "not a message")
                when (reply.type) {
                    expected -> return Opened(client, session, host, reply, viaInternet)
                    "error" -> throw AgentException(problemForCode(reply.str("code")), reply.str("msg"))
                    else -> throw AgentException(Problem.Protocol, "unexpected ${reply.type}")
                }
            } catch (e: AgentException) {
                withContext(NonCancellable) {
                    try {
                        session?.close()
                    } catch (_: Exception) {
                    }
                }
                client.close()
                throw e
            } catch (e: TimeoutCancellationException) {
                client.close()
                // Подключились, но агент молчит, — это не то же самое, что «нет связи».
                throw HostFailed(AgentException(if (session != null) Problem.Timeout else Problem.Unreachable, host, e))
            } catch (e: CancellationException) {
                client.close()
                throw e
            } catch (e: Exception) {
                client.close()
                throw HostFailed(
                    if (mismatch.value) AgentException(Problem.WrongPc, host, e)
                    else AgentException(Problem.Unreachable, "$host: ${e.message}", e),
                )
            }
        }
    }
}
