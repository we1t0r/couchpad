package app.remotedesk.android

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.lifecycleScope
import app.remotedesk.core.LinkState
import app.remotedesk.core.LocalFile
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import app.remotedesk.core.NsdPcDiscovery
import app.remotedesk.ui.App
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.flow.MutableStateFlow

class MainActivity : ComponentActivity() {
    private val incomingLinks = MutableStateFlow<String?>(null)
    private val incomingShares = MutableStateFlow<List<LocalFile>?>(null)
    private var onScanned: ((String?) -> Unit)? = null
    private val scanner = registerForActivityResult(ScanContract()) { result ->
        onScanned?.invoke(result.contents)
        onScanned = null
    }
    private lateinit var storage: AndroidStorage
    private val picker = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris -> storage.onPicked(uris) }
    private val folderPicker = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri -> couchpad.folders.onPicked(uri) }
    private lateinit var discovery: NsdPcDiscovery

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val app = couchpad
        storage = app.storage.also { it.picker = picker }
        app.folders.picker = folderPicker
        if (savedInstanceState == null) handle(intent)
        // Про уведомления спрашиваем, когда они впервые понадобятся: при первом подключении к компьютеру.
        lifecycleScope.launch {
            app.controller.state.first { it is LinkState.Connected }
            askNotificationsOnce()
        }

        val store = app.store
        val remoteSettings = app.remoteSettings
        val device = app.device
        discovery = NsdPcDiscovery(applicationContext)

        setContent {
            App(
                store = store,
                remoteSettings = remoteSettings,
                discovery = discovery,
                device = device,
                scanQr = { onResult ->
                    onScanned = onResult
                    scanner.launch(
                        ScanOptions()
                            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                            .setPrompt(getString(R.string.scan_prompt))
                            .setBeepEnabled(false)
                            .setOrientationLocked(false),
                    )
                },
                storage = storage,
                clipboard = AndroidClipboard(applicationContext),
                incomingLinks = incomingLinks,
                incomingShares = incomingShares,
                sharedController = app.controller,
                sharedTransfers = app.transfers,
                phoneFolders = app.folders,
            )
        }
    }

    private val notificationsPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    /**
     * Android 13+ без разрешения прячет уведомление о подключении и передаче файлов.
     * Спрашиваем один раз; откажут — всё работает, просто без уведомления.
     */
    private fun askNotificationsOnce() {
        if (Build.VERSION.SDK_INT < 33) return
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return
        val prefs = getSharedPreferences("ui", MODE_PRIVATE)
        if (prefs.getBoolean("asked_notifications", false)) return
        prefs.edit().putBoolean("asked_notifications", true).apply()
        notificationsPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    /** Ссылка rdesk:// (сопряжение) или «Поделиться» из другого приложения. */
    private fun handle(intent: Intent?) {
        when (intent?.action) {
            Intent.ACTION_SEND, Intent.ACTION_SEND_MULTIPLE -> {
                val files = sharedUris(intent).mapNotNull { AndroidStorage.localFile(this, it) }
                if (files.isNotEmpty()) incomingShares.value = files
            }
            else -> intent?.dataString?.let { incomingLinks.value = it }
        }
    }

    @Suppress("DEPRECATION")
    private fun sharedUris(intent: Intent): List<Uri> = when (intent.action) {
        Intent.ACTION_SEND -> listOfNotNull(
            if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
            else intent.getParcelableExtra(Intent.EXTRA_STREAM),
        )
        else -> (
            if (Build.VERSION.SDK_INT >= 33) intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
            else intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)
        ).orEmpty()
    }

    override fun onStart() {
        super.onStart()
        discovery.start()
    }

    override fun onStop() {
        discovery.stop()
        // Пока были в приложении, могли подключиться к другому компьютеру — виджет покажет его.
        RemoteWidget.refresh(applicationContext)
        super.onStop()
    }
}
