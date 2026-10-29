package app.remotedesk.core

import com.russhwolf.settings.MapSettings
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PairingLinkTest {
    private val sample = "rdesk://pair?v=1&id=0123456789abcdef&n=%D0%94%D0%BE%D0%BC%D0%B0%D1%88%D0%BD%D0%B8%D0%B9%20%D0%9F%D0%9A" +
        "&h=192.168.0.102,10.0.0.5&p=47800&fp=Ab-_cd&k=secret_Key-1"

    @Test
    fun parses_agent_link() {
        val link = PairingLink.parse(sample)!!
        assertEquals(1, link.version)
        assertEquals("0123456789abcdef", link.agentId)
        assertEquals("Домашний ПК", link.name)
        assertEquals(listOf("192.168.0.102", "10.0.0.5"), link.hosts)
        assertEquals(47800, link.port)
        assertEquals("Ab-_cd", link.fingerprint)
        assertEquals("secret_Key-1", link.secret)
    }

    @Test
    fun rejects_foreign_or_incomplete_links() {
        assertNull(PairingLink.parse("https://example.com/?v=1"))
        assertNull(PairingLink.parse("rdesk://pair?v=1&id=x&p=47800&fp=a&k=b"))
        assertNull(PairingLink.parse("rdesk://pair?v=1&id=x&h=1.2.3.4&p=99999&fp=a&k=b"))
        assertNull(PairingLink.parse(""))
        assertNull(PairingLink.parse(null))
    }

    @Test
    fun newer_protocol_is_flagged() {
        assertTrue(PairingLink.parse(sample.replace("v=1", "v=9"))!!.isNewerProtocol)
    }

    @Test
    fun decodes_broken_percent_as_text() {
        assertEquals("100%", PairingLink.percentDecode("100%"))
        assertEquals("a b", PairingLink.percentDecode("a+b"))
        assertEquals("😀x", PairingLink.percentDecode("😀x"))
    }

    @Test
    fun store_keeps_pcs_and_device_id() {
        val settings = MapSettings()
        val store = PcStore(settings)
        val pc = SavedPc("pc1", "ПК", listOf("192.168.0.2"), 47800, "fp", "token")
        store.save(pc)
        store.updateAddress("pc1", "fp", listOf("192.168.0.9"), 47801)
        store.updateAddress("pc1", "other-fp", listOf("6.6.6.6"), 1)
        val deviceId = store.device("Pixel", "android").id

        val reloaded = PcStore(settings)
        assertEquals(listOf("192.168.0.9", "192.168.0.2"), reloaded.get("pc1")!!.hosts)
        assertEquals(47801, reloaded.get("pc1")!!.port)
        assertEquals(deviceId, reloaded.device("Pixel", "android").id)

        reloaded.save(SavedPc("pc2", "Ноутбук", listOf("192.168.0.3"), 47800, "fp2", "t2"))
        reloaded.markUsed("pc1")
        assertEquals("pc1", PcStore(settings).lastUsed()!!.id)

        reloaded.markRevoked("pc1")
        assertTrue(PcStore(settings).get("pc1")!!.revoked)
        // Отозванный не годится для виджета — берём другой.
        assertEquals("pc2", PcStore(settings).lastUsed()!!.id)
    }
}
