package app.couchpad.core

import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

enum class PowerAction(internal val wire: String) {
    Shutdown("shutdown"),
    Restart("restart"),
    Sleep("sleep"),
    Hibernate("hibernate"),
    Lock("lock"),
    SignOut("signout"),
    MonitorsOff("monitors_off"),
    MonitorsOn("monitors_on"),
    ;

    /** Без «принудительно» Windows ждёт программы с несохранёнными данными. */
    val canForce: Boolean get() = this == Shutdown || this == Restart || this == SignOut

    companion object {
        fun parse(wire: String?): PowerAction? = entries.firstOrNull { it.wire == wire }
    }
}

/** Таймер на ПК. [deadline] — по часам телефона, с поправкой на расхождение часов с ПК. */
class PowerTimer(val action: PowerAction, val force: Boolean, val deadline: TimeMark) {
    val remainingMs: Long get() = (-deadline.elapsedNow().inWholeMilliseconds).coerceAtLeast(0)
}

/** Питание и мониторы ПК (см. «Питание и мониторы» в protocol/PROTOCOL.md). */
class RemotePower(private val send: (String) -> Unit) {
    /** Сейчас. [keepMonitorsOff] — для [PowerAction.MonitorsOff]: будить только кнопкой из приложения. */
    fun run(action: PowerAction, force: Boolean = false, keepMonitorsOff: Boolean = false) =
        send(message("power", "a" to action.wire, "force" to force.takeIf { it }, "keep" to keepMonitorsOff.takeIf { it }))

    fun schedule(action: PowerAction, inSeconds: Int, force: Boolean = false, keepMonitorsOff: Boolean = false) =
        send(
            message(
                "power",
                "a" to action.wire,
                "in" to inSeconds,
                "force" to force.takeIf { it },
                "keep" to keepMonitorsOff.takeIf { it },
            ),
        )

    fun cancel() = send(message("power_cancel"))

    internal companion object {
        /** Разбор power_timer; null — таймера нет. */
        fun parseTimer(msg: kotlinx.serialization.json.JsonObject): PowerTimer? {
            val action = PowerAction.parse(msg.str("a")) ?: return null
            val at = msg.num("at") ?: return null
            val now = msg.num("now") ?: return null
            val force = (msg["force"] as? kotlinx.serialization.json.JsonPrimitive)?.content == "true"
            return PowerTimer(action, force, TimeSource.Monotonic.markNow() + (at - now).milliseconds)
        }
    }
}
