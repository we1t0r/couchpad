package app.remotedesk.android

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.webkit.MimeTypeMap
import androidx.activity.result.ActivityResultLauncher
import androidx.documentfile.provider.DocumentFile
import app.remotedesk.core.DownloadTarget
import app.remotedesk.core.FileError
import app.remotedesk.core.LocalFile
import app.remotedesk.core.PhoneFolders
import app.remotedesk.core.PhoneShare
import app.remotedesk.core.RemoteEntry
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.OutputStream

/**
 * Папки телефона, открытые компьютеру. Каждую человек выбирает системным окном выбора папки
 * (Storage Access Framework): разрешение «доступ ко всем файлам» не нужно, а приложение видит только эти папки.
 */
class AndroidPhoneFolders(private val context: Context) : PhoneFolders {
    private class Saved(val share: PhoneShare, val uri: Uri)

    private val prefs = context.getSharedPreferences("phone_folders", Context.MODE_PRIVATE)
    private var saved: List<Saved> = load()
    private val _shares = MutableStateFlow(saved.map { it.share })
    override val shares: StateFlow<List<PhoneShare>> = _shares.asStateFlow()

    /** Запуск системного выбора папки; задаёт MainActivity. */
    var picker: ActivityResultLauncher<Uri?>? = null
    private var pending: CompletableDeferred<Uri?>? = null

    fun onPicked(uri: Uri?) {
        pending?.complete(uri)
        pending = null
    }

    override suspend fun addFolder(): PhoneShare? {
        val launcher = picker ?: return null
        val result = CompletableDeferred<Uri?>()
        pending?.complete(null)
        pending = result
        withContext(Dispatchers.Main) { launcher.launch(null) }
        val uri = result.await() ?: return null
        // Разрешение переживает перезапуск телефона; запись — только если человек её включит.
        context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        saved.firstOrNull { it.uri == uri }?.let { return it.share }
        val name = DocumentFile.fromTreeUri(context, uri)?.name ?: uri.lastPathSegment ?: "Папка"
        val share = PhoneShare("p-" + System.currentTimeMillis().toString(36), name, writable = false)
        update(saved + Saved(share, uri))
        return share
    }

    override fun setWritable(id: String, writable: Boolean) =
        update(saved.map { if (it.share.id == id) Saved(it.share.copy(writable = writable), it.uri) else it })

    override fun remove(id: String) {
        saved.firstOrNull { it.share.id == id }?.let {
            runCatching {
                context.contentResolver.releasePersistableUriPermission(it.uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            }
        }
        update(saved.filter { it.share.id != id })
    }

    override suspend fun list(root: String, path: String): List<RemoteEntry> = withContext(Dispatchers.IO) {
        val dir = resolve(root, path)
        if (!dir.isDirectory) throw FileError("not_found", "Папка не найдена")
        dir.listFiles().mapNotNull { f ->
            val name = f.name ?: return@mapNotNull null
            RemoteEntry(name, f.isDirectory, if (f.isDirectory) 0 else f.length(), f.lastModified())
        }
    }

    override suspend fun mkdir(root: String, path: String) = withContext(Dispatchers.IO) {
        val parent = resolve(root, parentOf(path))
        val name = nameOf(path)
        if (parent.findFile(name) != null) throw FileError("exists", "Такое имя уже есть")
        parent.createDirectory(name) ?: throw FileError("denied", "Телефон не дал создать папку")
        Unit
    }

    override suspend fun rename(root: String, path: String, name: String) = withContext(Dispatchers.IO) {
        checkName(name)
        val file = resolve(root, path)
        if (path.isEmpty()) throw FileError("root", "Саму открытую папку переименовать нельзя")
        if (resolve(root, parentOf(path)).findFile(name) != null) throw FileError("exists", "Такое имя уже есть")
        if (!file.renameTo(name)) throw FileError("denied", "Телефон не дал переименовать")
    }

    override suspend fun delete(root: String, path: String) = withContext(Dispatchers.IO) {
        if (path.isEmpty()) throw FileError("root", "Саму открытую папку удалить нельзя")
        if (!resolve(root, path).delete()) throw FileError("denied", "Телефон не дал удалить")
    }

    override suspend fun file(root: String, path: String): LocalFile = withContext(Dispatchers.IO) {
        val file = resolve(root, path)
        if (file.isDirectory) throw FileError("is_dir", "Папку целиком скачать пока нельзя — откройте её и выберите файлы")
        AndroidStorage.localFile(context, file.uri) ?: throw FileError("denied", "Телефон не дал прочитать файл")
    }

    override suspend fun createFile(root: String, folder: String, name: String, size: Long): DownloadTarget = withContext(Dispatchers.IO) {
        checkName(name)
        val dir = resolve(root, folder)
        val mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"
        // При совпадении имени Android сам назовёт файл «имя (1)».
        val file = dir.createFile(mime, name) ?: throw FileError("denied", "Телефон не дал создать файл")
        val out = context.contentResolver.openOutputStream(file.uri) ?: throw FileError("denied", "Телефон не дал записать файл")
        Target(out, "${saved.firstOrNull { it.share.id == root }?.share?.name.orEmpty()}/$folder".trimEnd('/')) { ok -> if (!ok) file.delete() }
    }

    private class Target(private val out: OutputStream, override val location: String, private val done: (Boolean) -> Unit) : DownloadTarget {
        override suspend fun write(buffer: ByteArray, count: Int) = withContext(Dispatchers.IO) { out.write(buffer, 0, count) }

        override suspend fun finish(ok: Boolean) = withContext(Dispatchers.IO) {
            runCatching { out.close() }
            done(ok)
        }
    }

    /** Файл или папка по пути внутри открытой папки. По «..» и в чужие папки не пройти: идём только вниз по именам. */
    private fun resolve(root: String, path: String): DocumentFile {
        val share = saved.firstOrNull { it.share.id == root } ?: throw FileError("no_root", "Эта папка больше не открыта компьютеру")
        var current = DocumentFile.fromTreeUri(context, share.uri)?.takeIf { it.exists() }
            ?: throw FileError("no_root", "Папка «${share.share.name}» недоступна — добавьте её в приложении заново")
        for (part in path.split('/').filter { it.isNotEmpty() }) {
            if (part == "." || part == "..") throw FileError("outside", "Путь ведёт за пределы открытой папки")
            current = current.findFile(part) ?: throw FileError("not_found", "Файл не найден")
        }
        return current
    }

    private fun parentOf(path: String) = path.substringBeforeLast('/', "")
    private fun nameOf(path: String) = path.substringAfterLast('/').also(::checkName)

    private fun checkName(name: String) {
        if (name.isBlank() || name == "." || name == ".." || name.contains('/')) throw FileError("bad_name", "Недопустимое имя")
    }

    private fun update(list: List<Saved>) {
        saved = list
        _shares.value = list.map { it.share }
        val json = JSONArray()
        list.forEach {
            json.put(JSONObject().put("id", it.share.id).put("name", it.share.name).put("writable", it.share.writable).put("uri", it.uri.toString()))
        }
        prefs.edit().putString("folders", json.toString()).apply()
    }

    private fun load(): List<Saved> = try {
        val json = JSONArray(prefs.getString("folders", "[]"))
        (0 until json.length()).map { i ->
            val o = json.getJSONObject(i)
            Saved(PhoneShare(o.getString("id"), o.getString("name"), o.optBoolean("writable")), Uri.parse(o.getString("uri")))
        }
    } catch (_: Exception) {
        emptyList()
    }
}
