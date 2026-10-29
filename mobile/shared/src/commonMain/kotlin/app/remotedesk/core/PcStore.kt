package app.remotedesk.core

import com.russhwolf.settings.Settings
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlin.random.Random

/** Сопряжённый компьютер, как его помнит телефон. */
@Serializable
data class SavedPc(
    val id: String,
    val name: String,
    val hosts: List<String>,
    val port: Int,
    val fingerprint: String,
    val token: String,
    val os: String = "",
    val agentVersion: String = "",
    val macs: List<MacInfo> = emptyList(),
    /** На компьютере отозвали доступ — нужно сопрячь заново. */
    val revoked: Boolean = false,
)

/** Список компьютеров и идентификатор телефона. Хранится в настройках приложения. */
class PcStore(private val settings: Settings) {
    private val _pcs = MutableStateFlow(load())
    val pcs: StateFlow<List<SavedPc>> = _pcs.asStateFlow()

    fun get(id: String): SavedPc? = _pcs.value.firstOrNull { it.id == id }

    /** Постоянный идентификатор этого телефона (не секрет, просто имя для агента). */
    fun device(name: String, platform: String): DeviceInfo {
        val id = settings.getStringOrNull(KEY_DEVICE_ID) ?: Random.nextBytes(16)
            .joinToString("") { (it.toInt() and 0xff).toString(16).padStart(2, '0') }
            .also { settings.putString(KEY_DEVICE_ID, it) }
        return DeviceInfo(id, name, platform)
    }

    /** Добавляет или заменяет компьютер с тем же id. */
    fun save(pc: SavedPc) = change { list -> listOf(pc) + list.filter { it.id != pc.id } }

    fun remove(id: String) = change { list -> list.filter { it.id != id } }

    fun markRevoked(id: String) = change { list -> list.map { if (it.id == id) it.copy(revoked = true) else it } }

    /** Компьютер нашёлся в сети по другому адресу (DHCP выдал новый IP). */
    fun updateAddress(id: String, fingerprint: String, hosts: List<String>, port: Int) = change { list ->
        list.map { pc ->
            if (pc.id != id || pc.fingerprint != fingerprint || hosts.isEmpty()) pc
            else pc.copy(hosts = (hosts + pc.hosts).distinct().take(MAX_HOSTS), port = port)
        }
    }

    /** После подключения обновляем то, что агент сообщил о себе. */
    fun updateInfo(id: String, agent: AgentInfo, host: String) = change { list ->
        list.map { pc ->
            if (pc.id != id) pc
            else pc.copy(
                name = agent.name,
                os = agent.os,
                agentVersion = agent.version,
                macs = agent.macs.ifEmpty { pc.macs },
                hosts = (listOf(host) + pc.hosts).distinct().take(MAX_HOSTS),
                revoked = false,
            )
        }
    }

    private fun change(transform: (List<SavedPc>) -> List<SavedPc>) {
        _pcs.update(transform)
        settings.putString(KEY_PCS, ProtocolJson.encodeToString(ListSerializer(SavedPc.serializer()), _pcs.value))
    }

    private fun load(): List<SavedPc> = try {
        settings.getStringOrNull(KEY_PCS)?.let { ProtocolJson.decodeFromString(ListSerializer(SavedPc.serializer()), it) }
    } catch (_: Exception) {
        null
    }.orEmpty()

    private companion object {
        const val KEY_PCS = "pcs"
        const val KEY_DEVICE_ID = "deviceId"
        const val MAX_HOSTS = 6
    }
}
