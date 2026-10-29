package app.remotedesk.android

import android.content.Context
import android.os.Build
import android.provider.Settings
import android.widget.Toast
import app.remotedesk.core.AgentConnection
import app.remotedesk.core.AgentException
import app.remotedesk.core.PcStore
import app.remotedesk.core.PowerAction
import app.remotedesk.core.Problem
import app.remotedesk.core.RemoteSettings
import app.remotedesk.core.WakeOnLan
import com.russhwolf.settings.SharedPreferencesSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout

/** Фоновые задачи приложения, которым не нужен экран (плитки, виджет). */
val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

fun Context.settingsStorage() = SharedPreferencesSettings(getSharedPreferences("remotedesk", Context.MODE_PRIVATE))

/** Имя, которое пользователь дал телефону, иначе «Производитель Модель». */
fun Context.deviceName(): String {
    val userName = runCatching { Settings.Global.getString(contentResolver, Settings.Global.DEVICE_NAME) }.getOrNull()
    if (!userName.isNullOrBlank()) return userName
    val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
    return if (Build.MODEL.startsWith(maker, ignoreCase = true)) Build.MODEL else "$maker ${Build.MODEL}"
}

/**
 * Одна команда без открытия приложения: подключиться к последнему компьютеру, выполнить, отключиться.
 * Результат — короткое сообщение для всплывающей подсказки.
 */
object QuickActions {
    private const val TIMEOUT_MS = 8_000L

    suspend fun run(context: Context, action: PowerAction): String {
        val storage = context.settingsStorage()
        val store = PcStore(storage)
        val pc = store.lastUsed() ?: return context.getString(R.string.qa_no_pc)
        val keep = action == PowerAction.MonitorsOff && RemoteSettings(storage).touchpad.value.monitorsWakeByAppOnly
        val device = store.device(context.deviceName(), "android")
        return try {
            withTimeout(TIMEOUT_MS) {
                val connection = AgentConnection.connect(pc, device)
                try {
                    connection.power.run(action, keepMonitorsOff = keep)
                    // pong приходит после того, как агент обработал команду.
                    connection.ping()
                } finally {
                    connection.close()
                }
            }
            context.getString(
                when (action) {
                    PowerAction.MonitorsOff -> R.string.qa_done_monitors_off
                    PowerAction.MonitorsOn -> R.string.qa_done_monitors_on
                    else -> R.string.qa_done_lock
                },
                pc.name,
            )
        } catch (e: AgentException) {
            if (e.problem == Problem.Revoked) store.markRevoked(pc.id)
            context.getString(R.string.qa_failed, pc.name, context.getString(shortProblem(e.problem)))
        } catch (_: TimeoutCancellationException) {
            context.getString(R.string.qa_failed, pc.name, context.getString(R.string.qa_problem_timeout))
        }
    }

    /** Включить последний компьютер по сети. Ждать загрузки не будем: процесс в фоне долго не живёт. */
    suspend fun wake(context: Context): String {
        val pc = PcStore(context.settingsStorage()).lastUsed() ?: return context.getString(R.string.qa_no_pc)
        if (!WakeOnLan.canWake(pc)) return context.getString(R.string.qa_wake_no_mac, pc.name)
        return if (WakeOnLan.wake(pc) > 0) context.getString(R.string.qa_wake_sent, pc.name)
        else context.getString(R.string.qa_wake_no_network)
    }

    /** [action] == null — включить ПК по сети. */
    suspend fun runAndToast(context: Context, action: PowerAction?) {
        val text = if (action == null) wake(context) else run(context, action)
        withContext(Dispatchers.Main) { Toast.makeText(context, text, Toast.LENGTH_LONG).show() }
    }

    private fun shortProblem(problem: Problem) = when (problem) {
        Problem.Unreachable -> R.string.qa_problem_unreachable
        Problem.WrongPc -> R.string.qa_problem_wrong_pc
        Problem.Revoked -> R.string.qa_problem_revoked
        Problem.Timeout -> R.string.qa_problem_timeout
        else -> R.string.qa_problem_other
    }
}
