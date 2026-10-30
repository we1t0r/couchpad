package app.remotedesk.android

import android.app.Application
import app.remotedesk.core.ConnectionController
import app.remotedesk.core.PcStore
import app.remotedesk.core.RemoteSettings
import app.remotedesk.core.Transfers
import kotlinx.coroutines.MainScope

/**
 * Всё, что должно пережить окно приложения: соединение с компьютером и передачи файлов.
 * Пока они идут, [RemoteService] держит процесс живым и показывает уведомление.
 */
class CouchpadApp : Application() {
    val scope = MainScope()
    val store by lazy { PcStore(settingsStorage()) }
    val remoteSettings by lazy { RemoteSettings(settingsStorage()) }
    val device by lazy { store.device(deviceName(), "android") }
    val storage by lazy { AndroidStorage(this) }
    val controller by lazy { ConnectionController(store, device, scope) }
    val transfers by lazy { Transfers(scope, storage) }

    override fun onCreate() {
        super.onCreate()
        RemoteService.watch(this)
    }
}

val android.content.Context.couchpad get() = applicationContext as CouchpadApp
