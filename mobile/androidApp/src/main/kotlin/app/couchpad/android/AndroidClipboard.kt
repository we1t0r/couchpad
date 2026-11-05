package app.couchpad.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.core.content.FileProvider
import app.couchpad.core.ClipImage
import app.couchpad.core.PhoneClipboard
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Буфер обмена Android. Картинку с ПК кладём файлом в кэш приложения и отдаём через FileProvider:
 * приложение, куда её вставят, получит право прочитать этот файл.
 */
class AndroidClipboard(private val context: Context) : PhoneClipboard {
    private val manager get() = context.getSystemService(ClipboardManager::class.java)

    override fun text(): String? {
        val clip = manager.primaryClip ?: return null
        if (clip.itemCount == 0) return null
        val item = clip.getItemAt(0)
        item.text?.toString()?.let { return it }
        // Ссылка на картинку или файл — это не текст (иначе на ПК уйдёт строка content://…).
        if (item.uri != null) return null
        return item.coerceToText(context)?.toString()
    }

    override fun setText(text: String) {
        manager.setPrimaryClip(ClipData.newPlainText("Couchpad", text))
    }

    override suspend fun image(): ClipImage? = withContext(Dispatchers.IO) {
        val clip = manager.primaryClip ?: return@withContext null
        if (clip.itemCount == 0) return@withContext null
        val uri = clip.getItemAt(0).uri ?: return@withContext null
        val mime = context.contentResolver.getType(uri) ?: clip.description.getMimeType(0)
        if (mime == null || !mime.startsWith("image/")) return@withContext null
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return@withContext null
        ClipImage(bytes, mime)
    }

    override suspend fun setImage(image: ClipImage) {
        val uri = withContext(Dispatchers.IO) {
            // Одна картинка за раз: прошлую стираем, чтобы кэш не рос.
            val dir = File(context.cacheDir, "clip").apply { mkdirs() }
            dir.listFiles()?.forEach { it.delete() }
            val ext = if (image.mime == "image/jpeg") "jpg" else "png"
            val file = File(dir, "clip-${System.currentTimeMillis()}.$ext").apply { writeBytes(image.bytes) }
            FileProvider.getUriForFile(context, "${context.packageName}.files", file)
        }
        manager.setPrimaryClip(ClipData.newUri(context.contentResolver, "Couchpad", uri))
    }
}
