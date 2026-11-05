package app.couchpad.core

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import java.net.Inet4Address
import java.util.concurrent.Executors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/** Поиск агентов через системный NsdManager (mDNS / DNS-SD). Google Play Services не нужны. */
class NsdPcDiscovery(context: Context) : PcDiscovery {
    private val nsd = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val executor = Executors.newSingleThreadExecutor()
    private val byService = mutableMapOf<String, FoundPc>()
    private val _found = MutableStateFlow<List<FoundPc>>(emptyList())
    override val found: StateFlow<List<FoundPc>> = _found.asStateFlow()

    private var listener: NsdManager.DiscoveryListener? = null
    private val infoCallbacks = mutableMapOf<String, NsdManager.ServiceInfoCallback>()

    // До Android 14 одновременно можно разрешать только одну службу — очередь.
    private val pending = ArrayDeque<NsdServiceInfo>()
    private var resolving = false

    @Synchronized
    override fun start() {
        if (listener != null) return
        val l = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) = synchronized(this@NsdPcDiscovery) { listener = null }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
            override fun onServiceFound(info: NsdServiceInfo) = resolve(info)
            override fun onServiceLost(info: NsdServiceInfo) = lost(info.serviceName)
        }
        listener = l
        try {
            nsd.discoverServices("${Protocol.SERVICE_TYPE}.", NsdManager.PROTOCOL_DNS_SD, l)
        } catch (_: Exception) {
            listener = null
        }
    }

    @Synchronized
    override fun stop() {
        listener?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        listener = null
        if (Build.VERSION.SDK_INT >= 34) infoCallbacks.values.forEach { runCatching { nsd.unregisterServiceInfoCallback(it) } }
        infoCallbacks.clear()
        pending.clear()
        resolving = false
        byService.clear()
        _found.value = emptyList()
    }

    @Synchronized
    private fun resolve(info: NsdServiceInfo) {
        if (Build.VERSION.SDK_INT >= 34) {
            if (info.serviceName in infoCallbacks) return
            val callback = object : NsdManager.ServiceInfoCallback {
                override fun onServiceInfoCallbackRegistrationFailed(errorCode: Int) = Unit
                override fun onServiceUpdated(info: NsdServiceInfo) = resolved(info, info.hostAddresses.filterIsInstance<Inet4Address>())
                override fun onServiceLost() = lost(info.serviceName)
                override fun onServiceInfoCallbackUnregistered() = Unit
            }
            infoCallbacks[info.serviceName] = callback
            runCatching { nsd.registerServiceInfoCallback(info, executor, callback) }
        } else {
            pending.addLast(info)
            resolveNext()
        }
    }

    @Suppress("DEPRECATION")
    @Synchronized
    private fun resolveNext() {
        if (resolving) return
        val next = pending.removeFirstOrNull() ?: return
        resolving = true
        nsd.resolveService(next, object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) = done()
            override fun onServiceResolved(info: NsdServiceInfo) {
                resolved(info, listOfNotNull(info.host as? Inet4Address))
                done()
            }

            fun done() {
                synchronized(this@NsdPcDiscovery) { resolving = false }
                resolveNext()
            }
        })
    }

    @Synchronized
    private fun resolved(info: NsdServiceInfo, addresses: List<Inet4Address>) {
        if (listener == null) return
        val txt = info.attributes.mapValues { (_, v) -> v?.decodeToString().orEmpty() }
        val id = txt["id"]?.takeIf { it.isNotBlank() } ?: return
        val fp = txt["fp"]?.takeIf { it.isNotBlank() } ?: return
        val hosts = addresses.mapNotNull { it.hostAddress }
        if (hosts.isEmpty()) return
        byService[info.serviceName] = FoundPc(id, txt["name"].orEmpty().ifBlank { info.serviceName }, hosts, info.port, fp)
        publish()
    }

    @Synchronized
    private fun lost(serviceName: String) {
        if (byService.remove(serviceName) != null) publish()
        if (Build.VERSION.SDK_INT >= 34) infoCallbacks.remove(serviceName)?.let { runCatching { nsd.unregisterServiceInfoCallback(it) } }
    }

    private fun publish() = _found.update { byService.values.distinctBy { it.id }.sortedBy { it.name.lowercase() } }
}
