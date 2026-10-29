package app.remotedesk.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Куда телефон сохраняет скачанное. */
interface DownloadTarget {
    /** Где искать файл — для подсказки пользователю («Загрузки/RemoteDesk»). */
    val location: String
    suspend fun write(buffer: ByteArray, count: Int)

    /** [ok] = false — недокачанное убрать. */
    suspend fun finish(ok: Boolean)
}

/** Открытый для чтения файл телефона. */
interface LocalReader : ReadChunk {
    fun close()
}

/** Файл телефона для отправки на ПК; [open] открывает его с нужного места (для докачки). */
class LocalFile(val name: String, val size: Long, val open: suspend (offset: Long) -> LocalReader)

/** Хранилище телефона — у каждой платформы своё. */
interface PhoneStorage {
    suspend fun createDownload(name: String, size: Long): DownloadTarget

    /** Системный выбор файлов; пустой список — пользователь передумал. */
    suspend fun pickFiles(): List<LocalFile>

    /** Открыть скачанный файл подходящим приложением телефона. */
    fun open(target: DownloadTarget)
}

enum class TransferState { Queued, Running, Done, Failed }

data class Transfer(
    val id: Long,
    val upload: Boolean,
    val name: String,
    val total: Long,
    val done: Long = 0,
    val state: TransferState = TransferState.Queued,
    /** Понятная причина ошибки. */
    val error: String? = null,
    /** Ошибка «такой файл уже есть» — можно спросить пользователя и повторить с [Conflict.Replace] или [Conflict.Rename]. */
    val exists: Boolean = false,
    val target: DownloadTarget? = null,
)

/**
 * Очередь передач: по одной за раз, с прогрессом и докачкой после обрыва (до 5 повторов).
 * Живёт, пока жив процесс приложения.
 */
class Transfers(private val scope: CoroutineScope, private val storage: PhoneStorage) {
    private val _items = MutableStateFlow<List<Transfer>>(emptyList())
    val items: StateFlow<List<Transfer>> = _items.asStateFlow()

    private val queue = Channel<suspend () -> Unit>(Channel.UNLIMITED)
    private val retryUploads = mutableMapOf<Long, suspend (Conflict) -> Unit>()
    private var nextId = 1L

    /** Сделано успешно — например, обновить список папки после отправки. */
    var onFinished: (Transfer) -> Unit = {}

    init {
        scope.launch { for (work in queue) work() }
    }

    fun download(files: AgentFiles, root: String, path: String, name: String, size: Long) {
        val id = add(Transfer(nextId++, upload = false, name = name, total = size))
        queue.trySend {
            val target = try {
                storage.createDownload(name, size)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                fail(id, e)
                return@trySend
            }
            change(id) { it.copy(state = TransferState.Running, target = target) }
            var done = 0L
            var reported = 0L
            // При обрыве продолжаем с [done]: всё, что до него, уже записано.
            val ok = withRetries(id) {
                files.download(root, path, done) { buffer, n ->
                    target.write(buffer, n)
                    done += n
                    if (done - reported >= REPORT_EVERY) {
                        reported = done
                        change(id) { it.copy(done = reported, error = null) }
                    }
                }
            }
            target.finish(ok)
            if (ok) finish(id, done)
        }
    }

    fun upload(files: AgentFiles, root: String, folder: String, file: LocalFile, conflict: Conflict = Conflict.Ask) {
        val id = add(Transfer(nextId++, upload = true, name = file.name, total = file.size))
        enqueueUpload(id, files, root, folder, file, conflict)
    }

    /** Повторить отправку, которая упала на «файл уже есть»: заменить или сохранить рядом под новым именем. */
    fun resolveConflict(id: Long, conflict: Conflict) {
        val retry = retryUploads.remove(id) ?: return
        change(id) { it.copy(state = TransferState.Queued, error = null, exists = false) }
        scope.launch { retry(conflict) }
    }

    fun clearFinished() = _items.update { list -> list.filter { it.state == TransferState.Queued || it.state == TransferState.Running } }

    fun open(transfer: Transfer) {
        transfer.target?.let(storage::open)
    }

    private fun enqueueUpload(id: Long, files: AgentFiles, root: String, folder: String, file: LocalFile, conflict: Conflict) {
        queue.trySend {
            change(id) { it.copy(state = TransferState.Running) }
            var name = file.name
            var reported = 0L
            val ok = withRetries(id) {
                val (finalName, have) = files.beginUpload(root, folder, name, conflict)
                name = finalName
                val reader = file.open(have)
                try {
                    files.upload(root, folder, name, file.size, have, reader) { sent ->
                        if (sent - reported >= REPORT_EVERY) {
                            reported = sent
                            change(id) { it.copy(done = sent, name = finalName) }
                        }
                    }
                } finally {
                    reader.close()
                }
            }
            if (ok) {
                finish(id, file.size)
            } else if (_items.value.firstOrNull { it.id == id }?.exists == true) {
                retryUploads[id] = { c -> enqueueUpload(id, files, root, folder, file, c) }
            }
        }
    }

    /** Повторяет при обрывах связи; ошибки агента (нет прав, нет места, конфликт) — сразу в ошибку. */
    private suspend fun withRetries(id: Long, attempt: suspend () -> Any?): Boolean {
        var tries = 0
        while (true) {
            try {
                attempt()
                return true
            } catch (e: CancellationException) {
                throw e
            } catch (e: FileError) {
                if (e.resumeOffset() != null && tries++ < MAX_TRIES) continue
                fail(id, e)
                return false
            } catch (e: AgentException) {
                fail(id, e)
                return false
            } catch (e: Exception) {
                if (tries++ >= MAX_TRIES) {
                    fail(id, e)
                    return false
                }
                change(id) { it.copy(error = "Связь прервалась — продолжаем… (${tries}/$MAX_TRIES)") }
                delay(2_000L * tries)
            }
        }
    }

    private fun add(t: Transfer): Long {
        _items.update { it + t }
        return t.id
    }

    private fun change(id: Long, f: (Transfer) -> Transfer) = _items.update { list -> list.map { if (it.id == id) f(it) else it } }

    private fun finish(id: Long, total: Long) {
        change(id) { it.copy(state = TransferState.Done, done = total, error = null) }
        _items.value.firstOrNull { it.id == id }?.let(onFinished)
    }

    private fun fail(id: Long, e: Exception) {
        val exists = e is FileError && e.code == "exists"
        val text = when (e) {
            is FileError -> e.message
            is AgentException -> when (e.problem) {
                Problem.Revoked -> "Доступ к компьютеру отозван"
                Problem.WrongPc -> "Ответил другой компьютер"
                else -> "Компьютер не отвечает"
            }
            else -> "Связь прервалась: ${e.message ?: e::class.simpleName}"
        }
        change(id) { it.copy(state = TransferState.Failed, error = text, exists = exists) }
    }

    private companion object {
        const val MAX_TRIES = 5
        const val REPORT_EVERY = 256 * 1024L
    }
}
