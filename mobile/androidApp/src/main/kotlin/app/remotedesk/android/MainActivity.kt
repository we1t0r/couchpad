package app.remotedesk.android

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import app.remotedesk.core.LocalFile
import app.remotedesk.core.NsdPcDiscovery
import app.remotedesk.core.PcStore
import app.remotedesk.core.RemoteSettings
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
    private lateinit var discovery: NsdPcDiscovery

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        storage = AndroidStorage(applicationContext).also { it.picker = picker }
        if (savedInstanceState == null) handle(intent)

        val prefs = settingsStorage()
        val store = PcStore(prefs)
        val remoteSettings = RemoteSettings(prefs)
        val device = store.device(deviceName(), "android")
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
                incomingLinks = incomingLinks,
                incomingShares = incomingShares,
            )
        }
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
