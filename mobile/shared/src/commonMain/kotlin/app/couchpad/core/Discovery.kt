package app.couchpad.core

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** Компьютер с агентом, найденный в локальной сети через mDNS. */
data class FoundPc(
    val id: String,
    val name: String,
    val hosts: List<String>,
    val port: Int,
    val fingerprint: String,
)

/** Поиск компьютеров в сети. На каждой платформе свой. */
interface PcDiscovery {
    val found: StateFlow<List<FoundPc>>
    fun start()
    fun stop()
}

/** Заглушка там, где поиска нет (тесты, пока что iOS). */
object NoDiscovery : PcDiscovery {
    override val found: StateFlow<List<FoundPc>> = MutableStateFlow(emptyList())
    override fun start() = Unit
    override fun stop() = Unit
}
