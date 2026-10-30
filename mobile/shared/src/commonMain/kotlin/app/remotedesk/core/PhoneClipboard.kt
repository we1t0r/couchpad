package app.remotedesk.core

/** Картинка из буфера обмена: байты и тип (например, image/png). */
class ClipImage(val bytes: ByteArray, val mime: String)

/** Что лежит в буфере обмена ПК: текст или картинка (её телефон забирает отдельно, см. [AgentFiles.clipImage]). */
data class PcClip(val text: String, val image: Boolean)

/** Буфер обмена телефона — у каждой платформы свой. */
interface PhoneClipboard {
    fun text(): String?
    fun setText(text: String)

    /** Картинка из буфера телефона или null, если там не картинка. */
    suspend fun image(): ClipImage?

    /** Положить картинку в буфер телефона. */
    suspend fun setImage(image: ClipImage)
}
