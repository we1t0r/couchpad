package app.couchpad.core

import kotlinx.coroutines.delay

/** Один пункт проверки на ПК. [state]: ok, warning, problem, unknown. Тексты присылает агент. */
data class WolItem(val id: String, val state: String, val title: String, val hint: String?)

/** Готовность ПК к включению по сети — что проверил агент. */
data class WolStatus(val ready: Boolean, val canFix: Boolean, val items: List<WolItem>) {
    internal companion object {
        fun parse(msg: kotlinx.serialization.json.JsonObject): WolStatus {
            val items = (msg["checks"] as? kotlinx.serialization.json.JsonArray).orEmpty().mapNotNull { e ->
                val o = e as? kotlinx.serialization.json.JsonObject ?: return@mapNotNull null
                WolItem(o.str("id").orEmpty(), o.str("state").orEmpty(), o.str("title").orEmpty(), o.str("hint"))
            }
            fun flag(key: String) = (msg[key] as? kotlinx.serialization.json.JsonPrimitive)?.content == "true"
            return WolStatus(flag("ready"), flag("canFix"), items)
        }
    }
}

/** Отправить UDP-пакет на каждый адрес и порт (широковещательная отправка разрешена). Ошибки отдельных адресов пропускаются. */
internal expect suspend fun sendUdp(packet: ByteArray, hosts: List<String>, ports: List<Int>): Int

/**
 * Wake-on-LAN: «магический пакет» — 6 байт 0xFF и 16 раз MAC-адрес. Сетевая карта выключенного ПК
 * узнаёт в нём свой адрес и включает компьютер. Широковещательный пакет доходит только в той же
 * сети, что и ПК (дома). Не из дома пакет уходит на адрес дома в интернете ([SavedPc.wakeHost]),
 * а роутер пересылает его на ПК (проброс UDP-порта и привязка IP к MAC на роутере).
 */
object WakeOnLan {
    private val PORTS = listOf(9, 7)
    const val INTERNET_PORT = 9

    /** Адрес дома и порт из [SavedPc.wakeHost]: «name.ddns.net», «1.2.3.4» или с «:порт». */
    fun internetTarget(pc: SavedPc): Pair<String, Int>? {
        val text = pc.wakeHost.trim().removePrefix("http://").removePrefix("https://").trimEnd('/')
        if (text.isEmpty() || text.any { it.isWhitespace() }) return null
        val port = text.substringAfterLast(':', "").takeIf { ':' in text }?.toIntOrNull()
        if (port != null && port !in 1..65535) return null
        val host = if (port != null) text.substringBeforeLast(':') else text
        return host.takeIf { it.isNotEmpty() && ':' !in it }?.let { it to (port ?: INTERNET_PORT) }
    }

    fun parseMac(text: String): ByteArray? {
        val hex = text.filter { it.isLetterOrDigit() }
        if (hex.length != 12) return null
        return ByteArray(6) { i -> hex.substring(i * 2, i * 2 + 2).toIntOrNull(16)?.toByte() ?: return null }
    }

    fun magicPacket(mac: ByteArray): ByteArray {
        require(mac.size == 6)
        return ByteArray(6) { 0xFF.toByte() } + ByteArray(16 * 6) { mac[it % 6] }
    }

    /** Куда слать: широковещательный адрес подсети ПК (от агента или по его IP как /24) и общий 255.255.255.255. */
    fun targets(pc: SavedPc): List<String> = buildList {
        pc.macs.forEach { m ->
            if (m.broadcast.isNotBlank()) add(m.broadcast)
            else if (m.ip.count { it == '.' } == 3) add(m.ip.substringBeforeLast('.') + ".255")
        }
        add("255.255.255.255")
    }.distinct()

    /** Можно ли будить этот ПК: агент сообщил хотя бы один MAC-адрес. */
    fun canWake(pc: SavedPc): Boolean = pc.macs.any { parseMac(it.mac) != null }

    /**
     * Отправить пакеты на все сетевые карты ПК (проводные первыми) несколько раз:
     * UDP может потеряться, а лишние пакеты безвредны.
     * @return сколько пакетов ушло; 0 — нет сети или MAC-адреса.
     */
    suspend fun wake(pc: SavedPc, repeats: Int = 3): Int {
        val packets = pc.macs.sortedByDescending { it.wired }.mapNotNull { parseMac(it.mac) }.distinctBy { it.toList() }.map(::magicPacket)
        val hosts = targets(pc)
        val internet = internetTarget(pc)
        var sent = 0
        repeat(repeats) { attempt ->
            if (attempt > 0) delay(150)
            packets.forEach {
                sent += sendUdp(it, hosts, PORTS)
                if (internet != null) sent += sendUdp(it, listOf(internet.first), listOf(internet.second))
            }
        }
        return sent
    }
}
