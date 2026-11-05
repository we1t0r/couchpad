package app.couchpad.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Папка телефона, открытая компьютеру. */
data class PhoneShare(val id: String, val name: String, val writable: Boolean)

/**
 * Что телефон показывает компьютеру: только папки, которые человек сам выбрал в приложении.
 * Пути — относительно папки, части через «/». Ошибки — [FileError] с понятным текстом.
 */
interface PhoneFolders {
    val shares: StateFlow<List<PhoneShare>>

    /** Системный выбор папки; null — человек передумал. */
    suspend fun addFolder(): PhoneShare?
    fun setWritable(id: String, writable: Boolean)
    fun remove(id: String)

    suspend fun list(root: String, path: String): List<RemoteEntry>
    suspend fun mkdir(root: String, path: String)
    suspend fun rename(root: String, path: String, name: String)
    suspend fun delete(root: String, path: String)

    /** Файл для отправки на ПК. */
    suspend fun file(root: String, path: String): LocalFile

    /** Новый файл в папке [folder] (при совпадении имени — «имя (2)»). */
    suspend fun createFile(root: String, folder: String, name: String, size: Long): DownloadTarget
}

/**
 * Пока телефон подключён, отвечает на запросы компьютера к папкам телефона ({"t":"pf"}, см. PROTOCOL.md).
 * Файлы туда и обратно идут обычными передачами через HTTPS — с докачкой и прогрессом на телефоне.
 */
fun servePhoneFolders(
    scope: CoroutineScope,
    controller: ConnectionController,
    store: PcStore,
    deviceId: String,
    transfers: Transfers,
    folders: PhoneFolders,
) {
    scope.launch {
        controller.state.collectLatest { state ->
            if (state !is LinkState.Connected) return@collectLatest
            val pc = store.get(state.pcId) ?: return@collectLatest
            val files = AgentFiles(pc.copy(hosts = (listOf(state.connection.host) + pc.hosts).distinct()), deviceId)
            for (request in state.connection.phoneFileRequests) {
                launch {
                    val reply = try {
                        handle(request, folders, files, transfers)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: FileError) {
                        buildJsonObject { put("error", e.message ?: e.code) }
                    } catch (e: Exception) {
                        buildJsonObject { put("error", e.message ?: "Ошибка на телефоне") }
                    }
                    state.connection.replyPhoneFiles(request.num("id") ?: 0, reply)
                }
            }
        }
    }
}

private suspend fun handle(request: JsonObject, folders: PhoneFolders, files: AgentFiles, transfers: Transfers): JsonObject {
    val root = request.str("root").orEmpty()
    val path = request.str("path").orEmpty()
    val ok = buildJsonObject { put("ok", true) }

    fun writable() {
        val share = folders.shares.value.firstOrNull { it.id == root } ?: throw FileError("no_root", "Эта папка больше не открыта компьютеру")
        if (!share.writable) throw FileError("read_only", "Папка «${share.name}» открыта только для чтения. Разрешить запись можно на телефоне: Couchpad → Настройки")
    }

    return when (request.str("op")) {
        "roots" -> buildJsonObject {
            put("roots", buildJsonArray {
                folders.shares.value.forEach { s -> add(buildJsonObject { put("id", s.id); put("name", s.name); put("writable", s.writable) }) }
            })
        }
        "list" -> buildJsonObject {
            put("entries", buildJsonArray {
                folders.list(root, path).forEach { e ->
                    add(buildJsonObject { put("name", e.name); put("dir", e.dir); put("size", e.size); put("modified", e.modified) })
                }
            })
        }
        "mkdir" -> { writable(); folders.mkdir(root, path); ok }
        "rename" -> { writable(); folders.rename(root, path, request.str("name").orEmpty()); ok }
        "delete" -> { writable(); folders.delete(root, path); ok }
        // ПК просит файл: отправляем его обычной загрузкой в папку, которую ПК завёл под этот приём.
        "push" -> {
            val file = folders.file(root, path)
            transfers.upload(files, request.str("target").orEmpty(), "", file, Conflict.Rename)
            ok
        }
        // ПК кладёт файл в папку телефона: скачиваем его, как «Отправить на телефон», но сюда.
        "pull" -> {
            writable()
            val id = request.str("file").orEmpty()
            val name = request.str("name").orEmpty()
            val size = request.num("size") ?: 0
            transfers.download(files, PcOffer.ROOT, id, name, size) { folders.createFile(root, path, name, size) }
            ok
        }
        else -> throw FileError("bad_message", "Неизвестная операция")
    }
}
