package app.remotedesk.core

import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.websocket.WebSockets
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

internal actual fun pinnedHttpClient(fingerprint: String, onMismatch: () -> Unit): HttpClient {
    val trust = PinningTrustManager(fingerprint, onMismatch)
    val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), SecureRandom()) }
    return HttpClient(OkHttp) {
        install(WebSockets)
        engine {
            config {
                sslSocketFactory(ssl.socketFactory, trust)
                // Имя хоста не проверяем: агент живёт по IP, доверие — только по отпечатку.
                hostnameVerifier { _, _ -> true }
                connectTimeout(4, TimeUnit.SECONDS)
                // OkHttp сам шлёт ping и рвёт соединение, если ответа нет.
                pingInterval(10, TimeUnit.SECONDS)
            }
        }
    }
}

/** SHA-256 от DER-сертификата в base64url без «=» — как в агенте. */
fun fingerprintOf(der: ByteArray): String =
    Base64.getUrlEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(der))

/** Доверяет ровно одному сертификату — тому, что был в QR-коде. */
private class PinningTrustManager(
    private val expected: String,
    private val onMismatch: () -> Unit,
) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("no certificate")
        if (fingerprintOf(leaf.encoded) != expected) {
            onMismatch()
            throw CertificateException("certificate fingerprint mismatch")
        }
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("client certificates are not used")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
