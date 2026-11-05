package app.couchpad.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlin.time.TimeSource

/** Что сейчас с соединением — то, что видит пользователь. */
sealed interface LinkState {
    data object Idle : LinkState

    data class Connecting(val pcId: String?, val name: String, val pairing: Boolean) : LinkState

    data class Connected(val pcId: String, val connection: AgentConnection, val pingMs: Long? = null) : LinkState

    data class Failed(val pcId: String?, val name: String, val problem: Problem, val detail: String?) : LinkState
}

/** Одно активное соединение на всё приложение: подключить, сопрячь, держать пинг, отключить. */
class ConnectionController(
    private val store: PcStore,
    private val device: DeviceInfo,
    private val scope: CoroutineScope,
    private val pingEveryMs: Long = 5_000,
) {
    private val _state = MutableStateFlow<LinkState>(LinkState.Idle)
    val state: StateFlow<LinkState> = _state.asStateFlow()
    private var job: Job? = null

    /**
     * [retryForMs] — сколько пытаться, если ПК не отвечает. Нужно после входа в Windows: помощник экрана входа
     * уже закрылся, а обычный агент запустится через несколько секунд.
     */
    fun connect(pcId: String, retryForMs: Long = 0) {
        val pc = store.get(pcId) ?: return
        start(LinkState.Connecting(pc.id, pc.name, pairing = false), retryForMs) {
            val connection = AgentConnection.connect(pc, device)
            store.updateInfo(pc.id, connection.agent, connection.host.takeUnless { connection.viaInternet })
            pc.id to connection
        }
    }

    fun pair(link: PairingLink) {
        start(LinkState.Connecting(link.agentId, link.name, pairing = true)) {
            val paired = AgentConnection.pair(link, device)
            store.save(paired.pc)
            paired.pc.id to paired.connection
        }
    }

    fun disconnect() {
        val current = _state.value
        job?.cancel()
        job = null
        _state.value = LinkState.Idle
        if (current is LinkState.Connected)
            scope.launch { current.connection.close() }
    }

    private fun start(connecting: LinkState.Connecting, retryForMs: Long = 0, open: suspend () -> Pair<String, AgentConnection>) {
        disconnect()
        _state.value = connecting
        job = scope.launch {
            val started = TimeSource.Monotonic.markNow()
            var opened: Pair<String, AgentConnection>? = null
            while (opened == null) {
                opened = try {
                    open()
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    val problem = (e as? AgentException)?.problem ?: Problem.Unreachable
                    if (problem !in listOf(Problem.Unreachable, Problem.Timeout) || started.elapsedNow().inWholeMilliseconds >= retryForMs) {
                        fail(connecting.pcId, connecting.name, e)
                        return@launch
                    }
                    delay(2_000)
                    null
                }
            }
            val (pcId, connection) = opened
            try {
                store.markUsed(pcId)
                _state.value = LinkState.Connected(pcId, connection)
                val watcher = launch {
                    val reason = connection.closed.filterNotNull().first()
                    lost(pcId, connection, reason)
                }
                while (true) {
                    val ms = try {
                        connection.ping()
                    } catch (e: AgentException) {
                        // Причину закрытия сообщит watcher; таймаут пинга сам по себе — тоже обрыв.
                        if (e.problem == Problem.Timeout) lost(pcId, connection, e)
                        break
                    }
                    (_state.value as? LinkState.Connected)?.let { if (it.connection === connection) _state.value = it.copy(pingMs = ms) }
                    delay(pingEveryMs)
                }
                watcher.join()
            } finally {
                connection.close()
            }
        }
    }

    /** Связь пропала. С экрана входа — это вход в Windows: ждём обычного агента и подключаемся к нему сами. */
    private fun lost(pcId: String, connection: AgentConnection, e: Exception) {
        if (connection.signIn) connect(pcId, retryForMs = 90_000) else fail(pcId, connection.agent.name, e)
    }

    private fun fail(pcId: String?, name: String, e: Exception) {
        val problem = (e as? AgentException)?.problem ?: Problem.Unreachable
        if (problem == Problem.Revoked && pcId != null) store.markRevoked(pcId)
        _state.value = LinkState.Failed(pcId, name, problem, e.message)
        job?.cancel()
    }
}
