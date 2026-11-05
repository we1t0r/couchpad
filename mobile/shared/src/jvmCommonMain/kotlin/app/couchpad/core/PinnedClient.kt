package app.couchpad.core

import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.websocket.WebSockets
import java.net.InetAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.concurrent.TimeUnit
import javax.net.SocketFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

internal actual fun pinnedHttpClient(fingerprint: String, onMismatch: () -> Unit): HttpClient {
    val trust = PinningTrustManager(fingerprint, onMismatch)
    val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), SecureRandom()) }
    return HttpClient(OkHttp) {
        install(WebSockets)
        engine {
            config {
                socketFactory(NoDelaySocketFactory)
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

/**
 * Сокеты без алгоритма Нейгла: каждое движение мыши уходит сразу, а не копится до подтверждения
 * предыдущего пакета. Иначе вместе с отложенным подтверждением Windows курсор отстаёт на десятки
 * и сотни миллисекунд.
 */
private object NoDelaySocketFactory : SocketFactory() {
    private val system = getDefault()

    private fun Socket.noDelay() = apply { tcpNoDelay = true }

    override fun createSocket(): Socket = system.createSocket().noDelay()
    override fun createSocket(host: String?, port: Int): Socket = system.createSocket(host, port).noDelay()
    override fun createSocket(host: String?, port: Int, localHost: InetAddress?, localPort: Int): Socket =
        system.createSocket(host, port, localHost, localPort).noDelay()
    override fun createSocket(host: InetAddress?, port: Int): Socket = system.createSocket(host, port).noDelay()
    override fun createSocket(address: InetAddress?, port: Int, localAddress: InetAddress?, localPort: Int): Socket =
        system.createSocket(address, port, localAddress, localPort).noDelay()
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
