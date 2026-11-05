package app.couchpad.core

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class WakeOnLanTest {
    @Test
    fun magic_packet_is_ff_and_16_macs() {
        val mac = WakeOnLan.parseMac("AA:BB:CC:DD:EE:FF")!!
        val packet = WakeOnLan.magicPacket(mac)
        assertEquals(102, packet.size)
        assertTrue(packet.take(6).all { it == 0xFF.toByte() })
        for (i in 0 until 16) assertContentEquals(mac, packet.copyOfRange(6 + i * 6, 12 + i * 6))
    }

    @Test
    fun mac_formats() {
        assertContentEquals(WakeOnLan.parseMac("aa-bb-cc-dd-ee-ff"), WakeOnLan.parseMac("AA:BB:CC:DD:EE:FF"))
        assertNull(WakeOnLan.parseMac("AA:BB:CC"))
        assertNull(WakeOnLan.parseMac("ZZ:7C:16:D7:75:E2"))
    }

    @Test
    fun targets_prefer_agent_broadcast_and_fall_back_to_24() {
        val pc = SavedPc(
            "pc", "ПК", listOf("192.168.0.102"), 47800, "fp", "t",
            macs = listOf(
                MacInfo("AA:BB:CC:DD:EE:FF", "Ethernet", "10.1.2.3", wired = true, broadcast = "10.1.255.255"),
                MacInfo("08:8A:F1:53:40:08", "Wi-Fi", "192.168.0.50"),
            ),
        )
        assertEquals(listOf("10.1.255.255", "192.168.0.255", "255.255.255.255"), WakeOnLan.targets(pc))
        assertTrue(WakeOnLan.canWake(pc))
        assertFalse(WakeOnLan.canWake(pc.copy(macs = emptyList())))
    }

    @Test
    fun internet_target_parses_name_ip_and_port() {
        val pc = SavedPc("pc", "ПК", emptyList(), 47800, "fp", "t")
        assertNull(WakeOnLan.internetTarget(pc))
        assertEquals("home.ddns.net" to 9, WakeOnLan.internetTarget(pc.copy(wakeHost = " home.ddns.net ")))
        assertEquals("203.0.113.5" to 4009, WakeOnLan.internetTarget(pc.copy(wakeHost = "203.0.113.5:4009")))
        assertEquals("home.ddns.net" to 9, WakeOnLan.internetTarget(pc.copy(wakeHost = "http://home.ddns.net/")))
        assertNull(WakeOnLan.internetTarget(pc.copy(wakeHost = "home.ddns.net:99999")))
        assertNull(WakeOnLan.internetTarget(pc.copy(wakeHost = "home ddns")))
    }
}
