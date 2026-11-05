package app.couchpad.core

/**
 * Содержимое QR-кода агента:
 * couchpad://pair?v=1&id=<agentId>&n=<имя ПК>&h=<ip1,ip2>&p=<порт>&fp=<отпечаток>&k=<ключ>
 */
data class PairingLink(
    val version: Int,
    val agentId: String,
    val name: String,
    val hosts: List<String>,
    val port: Int,
    val fingerprint: String,
    val secret: String,
) {
    /** Ссылка от более новой версии агента, которую это приложение может не понять. */
    val isNewerProtocol: Boolean get() = version > Protocol.VERSION

    companion object {
        private val PREFIXES = listOf("couchpad://pair?")

        /** null — это не ссылка Couchpad или в ней чего-то не хватает. */
        fun parse(text: String?): PairingLink? {
            val trimmed = text?.trim() ?: return null
            val prefix = PREFIXES.firstOrNull { trimmed.startsWith(it, ignoreCase = true) } ?: return null
            val query = trimmed.substring(prefix.length)
                .substringBefore('#')
                .split('&')
                .filter { it.isNotEmpty() }
                .associate { percentDecode(it.substringBefore('=')) to percentDecode(it.substringAfter('=', "")) }

            val version = query["v"]?.toIntOrNull() ?: return null
            val id = query["id"]?.takeIf { it.isNotBlank() } ?: return null
            val hosts = query["h"].orEmpty().split(',').map { it.trim() }.filter { it.isNotEmpty() }
            val port = query["p"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
            val fingerprint = query["fp"]?.takeIf { it.isNotBlank() } ?: return null
            val secret = query["k"]?.takeIf { it.isNotBlank() } ?: return null
            if (hosts.isEmpty()) return null
            val name = query["n"]?.takeIf { it.isNotBlank() } ?: id.take(8)
            return PairingLink(version, id, name, hosts, port, fingerprint, secret)
        }

        /** Раскодирует %XX (UTF-8) и «+» как пробел. Битые последовательности оставляет как есть. */
        internal fun percentDecode(s: String): String {
            if ('%' !in s && '+' !in s) return s
            val bytes = ArrayList<Byte>(s.length)
            var i = 0
            while (i < s.length) {
                val c = s[i]
                val hex = if (c == '%' && i + 2 < s.length) s.substring(i + 1, i + 3).toIntOrNull(16) else null
                when {
                    hex != null -> {
                        bytes.add(hex.toByte())
                        i += 3
                    }
                    c == '+' -> {
                        bytes.add(' '.code.toByte())
                        i++
                    }
                    else -> {
                        // Обычные символы копируем кусками, чтобы не разрывать суррогатные пары.
                        var end = i + 1
                        while (end < s.length && s[end] != '%' && s[end] != '+') end++
                        s.substring(i, end).encodeToByteArray().forEach { bytes.add(it) }
                        i = end
                    }
                }
            }
            return bytes.toByteArray().decodeToString()
        }
    }
}
