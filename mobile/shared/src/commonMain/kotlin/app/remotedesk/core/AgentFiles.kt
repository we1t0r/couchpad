package app.remotedesk.core

import io.ktor.client.HttpClient
import io.ktor.client.request.HttpRequestBuilder
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.parameter
import io.ktor.client.request.post
import io.ktor.client.request.prepareGet
import io.ktor.client.request.put
import io.ktor.client.request.setBody
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.bodyAsChannel
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.content.OutgoingContent
import io.ktor.http.isSuccess
import io.ktor.utils.io.ByteWriteChannel
import io.ktor.utils.io.readAvailable
import io.ktor.utils.io.writeFully
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull

data class RemoteRoot(val id: String, val name: String, val writable: Boolean)

data class RemoteEntry(val name: String, val dir: Boolean, val size: Long, val modified: Long)

/** Ошибка от агента: [code] для логики (например, «exists»), [message] — для человека. */
class FileError(val code: String, message: String, val status: Int = 0) : Exception(message)

/** Что конфликтует с уже существующим файлом: спросить, заменить или сохранить под новым именем. */
enum class Conflict(internal val wire: String) { Ask("ask"), Replace("replace"), Rename("rename") }

/** Источник для отправки: читает в буфер, возвращает сколько прочитал (-1 — конец). */
fun interface ReadChunk {
    suspend fun read(buffer: ByteArray): Int
}

/**
 * Файлы ПК по HTTPS (см. «Файлы» в protocol/PROTOCOL.md). Тот же закреплённый сертификат, что и у управления.
 * Адрес выбирается при первом запросе: первый из адресов ПК, который ответил.
 */
class AgentFiles(private val pc: SavedPc, private val deviceId: String) {
    private var mismatch = false
    private val client: HttpClient = pinnedHttpClient(pc.fingerprint) { mismatch = true }
    private var base: String? = null

    /** Пути внутри открытой папки — части через «/», корень — пустая строка. */
    companion object {
        const val CHUNK = 64 * 1024
        const val INBOX = "inbox"

        fun join(path: String, name: String) = if (path.isEmpty()) name else "$path/$name"
        fun parent(path: String) = path.substringBeforeLast('/', "")
    }

    private suspend fun baseUrl(): String {
        base?.let { return it }
        var last: Exception = AgentException(Problem.Unreachable)
        for (host in pc.hosts) {
            val url = "https://$host:${pc.port}/api/files/"
            try {
                val r = client.get(url + "roots") { auth() }
                check(r)
                base = url
                return url
            } catch (e: CancellationException) {
                throw e
            } catch (e: FileError) {
                throw e
            } catch (e: Exception) {
                last = if (mismatch) AgentException(Problem.WrongPc, host, e) else AgentException(Problem.Unreachable, host, e)
            }
        }
        throw last
    }

    private fun HttpRequestBuilder.auth() {
        header("X-RDesk-Device", deviceId)
        header("X-RDesk-Token", pc.token)
    }

    private suspend fun check(r: HttpResponse): HttpResponse {
        if (r.status.isSuccess()) return r
        val body = runCatching { ProtocolJson.parseToJsonElement(r.bodyAsText()) as? JsonObject }.getOrNull()
        val code = body?.str("code") ?: "http_${r.status.value}"
        if (code == Protocol.Errors.REVOKED) throw AgentException(Problem.Revoked, body?.str("msg"))
        throw FileError(code, body?.str("msg") ?: r.status.description, r.status.value)
    }

    private suspend fun json(r: HttpResponse): JsonObject = ProtocolJson.parseToJsonElement(check(r).bodyAsText()) as JsonObject

    suspend fun roots(): List<RemoteRoot> {
        val o = json(client.get(baseUrl() + "roots") { auth() })
        return (o["roots"] as? JsonArray).orEmpty().mapNotNull { e ->
            val r = e as? JsonObject ?: return@mapNotNull null
            RemoteRoot(r.str("id") ?: return@mapNotNull null, r.str("name").orEmpty(), r.flag("writable"))
        }
    }

    suspend fun list(root: String, path: String): List<RemoteEntry> {
        val o = json(client.get(baseUrl() + "list") { auth(); parameter("root", root); parameter("path", path) })
        return (o["entries"] as? JsonArray).orEmpty().mapNotNull { e ->
            val r = e as? JsonObject ?: return@mapNotNull null
            RemoteEntry(r.str("name") ?: return@mapNotNull null, r.flag("dir"), r.num("size") ?: 0, r.num("modified") ?: 0)
        }
    }

    private suspend fun op(name: String, root: String, path: String, vararg extra: Pair<String, String>) {
        json(client.post(baseUrl() + name) {
            auth()
            parameter("root", root)
            parameter("path", path)
            extra.forEach { (k, v) -> parameter(k, v) }
        })
    }

    suspend fun mkdir(root: String, path: String) = op("mkdir", root, path)
    suspend fun rename(root: String, path: String, newName: String) = op("rename", root, path, "name" to newName)
    suspend fun delete(root: String, path: String) = op("delete", root, path)
    suspend fun openOnPc(root: String, path: String) = op("open", root, path)

    /**
     * Скачать с места [offset] (докачка — заголовок Range). [write] получает куски по порядку.
     * @return сколько байт получено за этот вызов.
     */
    suspend fun download(root: String, path: String, offset: Long, write: suspend (ByteArray, Int) -> Unit): Long {
        var got = 0L
        client.prepareGet(baseUrl() + "download") {
            auth()
            parameter("root", root)
            parameter("path", path)
            if (offset > 0) header(HttpHeaders.Range, "bytes=$offset-")
        }.execute { response ->
            check(response)
            // Сервер мог проигнорировать Range — тогда начинать с нуля нельзя: сообщаем.
            if (offset > 0 && response.status.value != 206) throw FileError("no_range", "Компьютер не поддерживает докачку")
            val channel = response.bodyAsChannel()
            val buffer = ByteArray(CHUNK)
            while (true) {
                val n = channel.readAvailable(buffer)
                if (n < 0) break
                if (n == 0) continue
                write(buffer, n)
                got += n
            }
        }
        return got
    }

    /** Начать отправку: итоговое имя (с учётом конфликта) и сколько уже принято (для докачки). */
    suspend fun beginUpload(root: String, folder: String, name: String, conflict: Conflict): Pair<String, Long> {
        val o = json(client.post(baseUrl() + "upload/begin") {
            auth()
            parameter("root", root)
            parameter("path", folder)
            parameter("name", name)
            parameter("conflict", conflict.wire)
        })
        return (o.str("name") ?: name) to (o.num("have") ?: 0)
    }

    /** Отправить данные начиная с [offset]; [source] уже стоит на этом месте. Возвращает, сколько принято всего. */
    suspend fun upload(
        root: String,
        folder: String,
        name: String,
        size: Long,
        offset: Long,
        source: ReadChunk,
        onSent: (Long) -> Unit,
    ): Long {
        val o = json(client.put(baseUrl() + "upload") {
            auth()
            parameter("root", root)
            parameter("path", folder)
            parameter("name", name)
            parameter("size", size)
            parameter("offset", offset)
            setBody(object : OutgoingContent.WriteChannelContent() {
                override val contentLength: Long = size - offset
                override val contentType: ContentType = ContentType.Application.OctetStream

                override suspend fun writeTo(channel: ByteWriteChannel) {
                    val buffer = ByteArray(CHUNK)
                    var left = size - offset
                    var sent = offset
                    while (left > 0) {
                        val n = source.read(buffer)
                        if (n < 0) throw FileError("source_short", "Файл на телефоне оказался короче, чем ожидалось")
                        val take = minOf(n.toLong(), left).toInt()
                        channel.writeFully(buffer, 0, take)
                        left -= take
                        sent += take
                        onSent(sent)
                    }
                }
            })
        })
        return o.num("have") ?: 0
    }

    fun close() = client.close()

    private fun JsonObject.flag(key: String) = (this[key] as? JsonPrimitive)?.booleanOrNull ?: false
}

/** Сколько уже принято, если агент ответил 409 «offset:N». */
fun FileError.resumeOffset(): Long? = if (code.startsWith("offset:")) code.removePrefix("offset:").toLongOrNull() else null
