package app.remotedesk.core

import io.ktor.client.HttpClient
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import io.ktor.client.plugins.websocket.webSocketSession
import io.ktor.websocket.Frame
import io.ktor.websocket.close
import io.ktor.websocket.readText
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.onSubscription
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonObject
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
                val msg = (frame as? Frame.Text)?.let { parseMessage(it.readText()) } ?: continue
                when (msg.type) {
                    "pong" -> msg.num("n")?.let { pongs.emit(it) }
                    "error" -> {
                        val problem = problemForCode(msg.str("code"))
                        // Ошибка, после которой агент закрывает соединение, — запоминаем её как причину.
                        if (problem == Problem.Revoked || problem == Problem.Incompatible)
                            reason = AgentException(problem, msg.str("msg"))
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
            val opened = open(link.hosts, link.port, link.fingerprint, hello, expected = "paired")
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
            val opened = open(pc.hosts, pc.port, pc.fingerprint, hello, expected = "welcome")
            val agent = opened.reply.agentInfo()
            if (agent == null || agent.id != pc.id) {
                opened.dispose()
                throw AgentException(Problem.WrongPc)
            }
            return AgentConnection(opened.client, opened.session, opened.host, agent, opened.reply.strings("permissions"))
        }

        private class Opened(
            val client: HttpClient,
            val session: DefaultClientWebSocketSession,
            val host: String,
            val reply: JsonObject,
        ) {
            suspend fun dispose() {
                try {
                    session.close()
                } catch (_: Exception) {
                }
                client.close()
            }
        }

        /** Пробует адреса по очереди. Ответ агента с ошибкой — окончательный, другие адреса уже не пробуем. */
        private suspend fun open(
            hosts: List<String>,
            port: Int,
            fingerprint: String,
            first: String,
            expected: String,
        ): Opened {
            var failure = AgentException(Problem.Unreachable)
            for (host in hosts.distinct()) {
                val mismatch = MutableStateFlow(false)
                val client = pinnedHttpClient(fingerprint) { mismatch.value = true }
                var session: DefaultClientWebSocketSession? = null
                try {
                    session = withTimeout(CONNECT_TIMEOUT_MS) { client.webSocketSession("wss://$host:$port/ws") }
                    session.send(Frame.Text(first))
                    val frame = withTimeout(CONNECT_TIMEOUT_MS) { session.incoming.receive() }
                    val reply = (frame as? Frame.Text)?.let { parseMessage(it.readText()) }
                        ?: throw AgentException(Problem.Protocol, "not a message")
                    when (reply.type) {
                        expected -> return Opened(client, session, host, reply)
                        "error" -> throw AgentException(problemForCode(reply.str("code")), reply.str("msg"))
                        else -> throw AgentException(Problem.Protocol, "unexpected ${reply.type}")
                    }
                } catch (e: AgentException) {
                    try {
                        session?.close()
                    } catch (_: Exception) {
                    }
                    client.close()
                    throw e
                } catch (e: TimeoutCancellationException) {
                    client.close()
                    // Подключились, но агент молчит, — это не то же самое, что «нет связи».
                    failure = AgentException(if (session != null) Problem.Timeout else Problem.Unreachable, host, e)
                } catch (e: CancellationException) {
                    client.close()
                    throw e
                } catch (e: Exception) {
                    client.close()
                    if (mismatch.value) {
                        failure = AgentException(Problem.WrongPc, host, e)
                    } else if (failure.problem != Problem.WrongPc) {
                        failure = AgentException(Problem.Unreachable, "$host: ${e.message}", e)
                    }
                }
            }
            throw failure
        }
    }
}
