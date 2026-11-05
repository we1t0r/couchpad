package app.couchpad.android

import android.app.Application
import app.couchpad.core.ConnectionController
import app.couchpad.core.PcStore
import app.couchpad.core.RemoteSettings
import app.couchpad.core.Transfers
import app.couchpad.core.receiveFromPc
import app.couchpad.core.servePhoneFolders
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
    val folders by lazy { AndroidPhoneFolders(this) }

    override fun onCreate() {
        super.onCreate()
        RemoteService.watch(this)
        receiveFromPc(scope, controller, store, device.id, transfers)
        servePhoneFolders(scope, controller, store, device.id, transfers, folders)
    }
}

val android.content.Context.couchpad get() = applicationContext as CouchpadApp
