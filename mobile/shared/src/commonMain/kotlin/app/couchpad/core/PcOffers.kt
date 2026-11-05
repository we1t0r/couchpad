package app.couchpad.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject

/** Файл, который компьютер отправил на телефон: скачивается из корня «outbox» по [id]. */
data class PcOffer(val id: String, val name: String, val size: Long) {
    companion object {
        const val ROOT = "outbox"

        internal fun parse(msg: JsonObject): List<PcOffer> = (msg["files"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = e as? JsonObject ?: return@mapNotNull null
            PcOffer(o.str("id") ?: return@mapNotNull null, o.str("name") ?: return@mapNotNull null, o.num("size") ?: 0)
        }
    }
}

/**
 * Пока телефон подключён к компьютеру, принимает файлы, которые тот присылает сам
 * («Отправить на телефон» в Проводнике), и ставит их в очередь скачивания — в «Загрузки».
 */
fun receiveFromPc(scope: CoroutineScope, controller: ConnectionController, store: PcStore, deviceId: String, transfers: Transfers) {
    scope.launch {
        controller.state.collectLatest { state ->
            if (state !is LinkState.Connected) return@collectLatest
            val pc = store.get(state.pcId) ?: return@collectLatest
            // Файлы — по тому же адресу, по которому сейчас есть связь.
            val files = AgentFiles(pc.copy(hosts = (listOf(state.connection.host) + pc.hosts).distinct()), deviceId)
            for (batch in state.connection.offers) {
                batch.forEach { transfers.download(files, PcOffer.ROOT, it.id, it.name, it.size) }
            }
        }
    }
}
