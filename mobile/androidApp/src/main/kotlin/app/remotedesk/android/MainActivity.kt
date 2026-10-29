package app.remotedesk.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import app.remotedesk.core.NsdPcDiscovery
import app.remotedesk.core.PcStore
import app.remotedesk.core.RemoteSettings
import app.remotedesk.ui.App
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.flow.MutableStateFlow

class MainActivity : ComponentActivity() {
    private val incomingLinks = MutableStateFlow<String?>(null)
    private var onScanned: ((String?) -> Unit)? = null
    private val scanner = registerForActivityResult(ScanContract()) { result ->
        onScanned?.invoke(result.contents)
        onScanned = null
    }
    private lateinit var discovery: NsdPcDiscovery

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        if (savedInstanceState == null) incomingLinks.value = intent?.dataString

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
                incomingLinks = incomingLinks,
            )
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.dataString?.let { incomingLinks.value = it }
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
