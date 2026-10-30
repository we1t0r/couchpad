package app.remotedesk.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import app.remotedesk.core.LinkState
import app.remotedesk.core.Transfer
import app.remotedesk.core.TransferState
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/**
 * Держит соединение с компьютером и передачи файлов, когда приложение свёрнуто.
 * Работает, пока есть подключение или идёт передача; в уведомлении — что происходит и кнопка «Отключить».
 */
class RemoteService : Service() {
    private var job: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val app = couchpad
        if (intent?.action == ACTION_DISCONNECT) {
            app.controller.disconnect()
            return START_NOT_STICKY
        }
        // startForeground обязателен в первые секунды после запуска — даже если показывать уже нечего.
        val type = if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification(app.controller.state.value, app.transfers.items.value), type)
        if (job == null) {
            job = app.scope.launch {
                combine(app.controller.state, app.transfers.items) { state, items -> state to items }
                    .map { (state, items) -> Content.of(state, items) }
                    .distinctUntilChanged()
                    .collect { content ->
                        if (content == null) {
                            stopSelf()
                        } else {
                            getSystemService(NotificationManager::class.java)
                                .notify(NOTIFICATION_ID, notification(app.controller.state.value, app.transfers.items.value))
                        }
                    }
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        job?.cancel()
        job = null
        super.onDestroy()
    }

    /** Что показать в уведомлении; null — сервис больше не нужен. Проценты округлены, чтобы не обновлять его на каждый килобайт. */
    private data class Content(val pc: String?, val transfer: String?, val percent: Int, val transfers: Int) {
        companion object {
            fun of(state: LinkState, items: List<Transfer>): Content? {
                val pc = (state as? LinkState.Connected)?.connection?.agent?.name
                val active = items.filter { it.state == TransferState.Running || it.state == TransferState.Queued }
                if (pc == null && active.isEmpty()) return null
                val current = active.firstOrNull { it.state == TransferState.Running } ?: active.firstOrNull()
                val percent = current?.let { if (it.total > 0) (it.done * 100 / it.total).toInt() else 0 } ?: 0
                return Content(pc, current?.name, percent / 5 * 5, active.size)
            }
        }
    }

    private fun notification(state: LinkState, items: List<Transfer>): Notification {
        createChannel(this)
        val content = Content.of(state, items)
        val open = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val builder = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_monitor)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setContentIntent(open)
            .setContentTitle(
                content?.pc?.let { getString(R.string.notif_connected, it) } ?: getString(R.string.notif_transfers),
            )
        if (content?.transfer != null) {
            val more = if (content.transfers > 1) " (+${content.transfers - 1})" else ""
            builder.setContentText("${content.transfer}$more")
                .setProgress(100, content.percent, false)
        } else {
            builder.setContentText(getString(R.string.notif_tap))
        }
        if (content?.pc != null) {
            val disconnect = PendingIntent.getService(
                this, 1,
                Intent(this, RemoteService::class.java).setAction(ACTION_DISCONNECT),
                PendingIntent.FLAG_IMMUTABLE,
            )
            builder.addAction(0, getString(R.string.notif_disconnect), disconnect)
        }
        return builder.build()
    }

    companion object {
        private const val CHANNEL = "connection"
        private const val NOTIFICATION_ID = 1
        private const val ACTION_DISCONNECT = "app.remotedesk.DISCONNECT"

        private fun createChannel(context: Context) {
            val channel = NotificationChannel(CHANNEL, context.getString(R.string.notif_channel), NotificationManager.IMPORTANCE_LOW)
            context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }

        /** Запускает сервис, как только появилось подключение или передача. Останавливается он сам. */
        fun watch(app: CouchpadApp) {
            app.scope.launch {
                combine(app.controller.state, app.transfers.items) { state, items -> Content.of(state, items) != null }
                    .distinctUntilChanged()
                    .collect { needed ->
                        if (!needed) return@collect
                        try {
                            ContextCompat.startForegroundService(app, Intent(app, RemoteService::class.java))
                        } catch (_: IllegalStateException) {
                            // Android 12+ не даёт запускать такой сервис из фона. Тогда передача идёт,
                            // пока жив процесс, — обычно это и так «пока приложение открыто».
                        }
                    }
            }
        }
    }
}
