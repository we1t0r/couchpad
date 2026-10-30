package app.remotedesk.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put

/** Константы протокола (см. protocol/PROTOCOL.md). */
object Protocol {
    const val VERSION = 1
    const val DEFAULT_PORT = 47800
    const val SERVICE_TYPE = "_rdesk._tcp"
    const val APP_VERSION = "0.1.0"

    object Errors {
        const val BAD_SECRET = "bad_secret"
        const val SECRET_EXPIRED = "secret_expired"
        const val TOO_MANY_ATTEMPTS = "too_many_attempts"
        const val NOT_AUTHENTICATED = "not_authenticated"
        const val REVOKED = "revoked"
        const val BAD_MESSAGE = "bad_message"
        const val UNSUPPORTED_VERSION = "unsupported_version"
    }
}

@Serializable
data class MacInfo(
    val mac: String,
    val name: String = "",
    val ip: String = "",
    val wired: Boolean = false,
    /** Широковещательный адрес подсети ПК, например 192.168.0.255. */
    val broadcast: String = "",
)

@Serializable
data class AgentInfo(
    val id: String,
    val name: String,
    val os: String = "",
    val version: String = "",
    val protocol: Int = Protocol.VERSION,
    val macs: List<MacInfo> = emptyList(),
)

/** Этот телефон с точки зрения агента. */
data class DeviceInfo(val id: String, val name: String, val platform: String)

internal val ProtocolJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
}

/** Сообщение {"t": type, ...}; поля со значением null пропускаются. */
internal fun message(type: String, vararg fields: Pair<String, Any?>): String = buildJsonObject {
    put("t", type)
    for ((key, value) in fields) {
        when (value) {
            null -> Unit
            is String -> put(key, value)
            is Number -> put(key, value)
            is Boolean -> put(key, value)
            is JsonElement -> put(key, value)
            else -> error("Неподдерживаемый тип поля $key")
        }
    }
}.toString()

/** null — не JSON-объект или нет поля t. */
internal fun parseMessage(text: String): JsonObject? = try {
    ProtocolJson.parseToJsonElement(text).jsonObject.takeIf { it.str("t") != null }
} catch (_: Exception) {
    null
}

internal fun JsonObject.str(key: String): String? = (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull

internal fun JsonObject.num(key: String): Long? = (this[key] as? JsonPrimitive)?.longOrNull

internal fun JsonObject.bool(key: String): Boolean? = (this[key] as? JsonPrimitive)?.takeIf { !it.isString }?.booleanOrNull

internal val JsonObject.type: String get() = str("t")!!

internal fun JsonObject.agentInfo(): AgentInfo? = try {
    this["agent"]?.let { ProtocolJson.decodeFromJsonElement(AgentInfo.serializer(), it) }
} catch (_: Exception) {
    null
}

internal fun JsonObject.strings(key: String): List<String> = try {
    (this[key] as? kotlinx.serialization.json.JsonArray)?.mapNotNull { it.jsonPrimitive.contentOrNull }.orEmpty()
} catch (_: Exception) {
    emptyList()
}
