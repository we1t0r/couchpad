package app.remotedesk.android

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import android.widget.Toast
import androidx.activity.result.ActivityResultLauncher
import androidx.core.content.FileProvider
import app.remotedesk.core.DownloadTarget
import app.remotedesk.core.LocalFile
import app.remotedesk.core.LocalReader
import app.remotedesk.core.PhoneStorage
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.InputStream
import java.io.OutputStream

/**
 * Файлы телефона: скачанное — в «Загрузки/Couchpad» (через MediaStore, без разрешений),
 * отправка — через системный выбор файлов.
 */
class AndroidStorage(private val context: Context) : PhoneStorage {
    /** Запуск системного выбора файлов; задаёт MainActivity. */
    var picker: ActivityResultLauncher<Array<String>>? = null
    private var pending: CompletableDeferred<List<Uri>>? = null

    fun onPicked(uris: List<Uri>) {
        pending?.complete(uris)
        pending = null
    }

    override suspend fun pickFiles(): List<LocalFile> {
        val launcher = picker ?: return emptyList()
        val result = CompletableDeferred<List<Uri>>()
        pending?.complete(emptyList())
        pending = result
        withContext(Dispatchers.Main) { launcher.launch(arrayOf("*/*")) }
        return result.await().mapNotNull { localFile(context, it) }
    }

    override suspend fun createDownload(name: String, size: Long): DownloadTarget = withContext(Dispatchers.IO) {
        val mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"
        if (Build.VERSION.SDK_INT >= 29) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Couchpad")
                // Пока качается, файл не виден другим приложениям.
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val resolver = context.contentResolver
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: error("Не удалось создать файл в «Загрузках»")
            val out = resolver.openOutputStream(uri) ?: error("Не удалось открыть файл для записи")
            Target(out, uri, mime, "Загрузки/Couchpad") { ok ->
                if (ok) resolver.update(uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
                else resolver.delete(uri, null, null)
            }
        } else {
            // Android 8–9: папка приложения, разрешение на память не нужно.
            val dir = File(context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "Couchpad").apply { mkdirs() }
            var file = File(dir, name)
            var i = 2
            while (file.exists()) file = File(dir, "${name.substringBeforeLast('.')} (${i++})" + name.substringAfterLast('.', "").let { if (it.isEmpty()) "" else ".$it" })
            val uri = FileProvider.getUriForFile(context, context.packageName + ".files", file)
            Target(file.outputStream(), uri, mime, file.parent ?: "") { ok -> if (!ok) file.delete() }
        }
    }

    override fun open(target: DownloadTarget) {
        val t = target as? Target ?: return
        val intent = Intent(Intent.ACTION_VIEW)
            .setDataAndType(t.uri, t.mime)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        try {
            context.startActivity(intent)
        } catch (_: Exception) {
            Toast.makeText(context, context.getString(R.string.no_app_to_open), Toast.LENGTH_LONG).show()
        }
    }

    private class Target(
        private val out: OutputStream,
        val uri: Uri,
        val mime: String,
        override val location: String,
        private val done: (Boolean) -> Unit,
    ) : DownloadTarget {
        override suspend fun write(buffer: ByteArray, count: Int) = withContext(Dispatchers.IO) { out.write(buffer, 0, count) }

        override suspend fun finish(ok: Boolean) = withContext(Dispatchers.IO) {
            runCatching { out.close() }
            done(ok)
        }
    }

    companion object {
        /** Описание файла по content:// — имя и размер; открывается с нужного места для докачки. */
        fun localFile(context: Context, uri: Uri): LocalFile? {
            val resolver = context.contentResolver
            var name = uri.lastPathSegment ?: "file"
            var size = -1L
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    c.getString(0)?.let { name = it }
                    if (!c.isNull(1)) size = c.getLong(1)
                }
            }
            if (size < 0) {
                size = resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1
            }
            if (size < 0) return null
            return LocalFile(name.replace('/', '_'), size) { offset ->
                withContext(Dispatchers.IO) {
                    val input = resolver.openInputStream(uri) ?: error("Не удалось открыть файл")
                    var skipped = 0L
                    while (skipped < offset) {
                        val n = input.skip(offset - skipped)
                        if (n <= 0) break
                        skipped += n
                    }
                    Reader(input)
                }
            }
        }
    }

    private class Reader(private val input: InputStream) : LocalReader {
        override suspend fun read(buffer: ByteArray): Int = withContext(Dispatchers.IO) { input.read(buffer) }
        override fun close() = input.close()
    }
}
