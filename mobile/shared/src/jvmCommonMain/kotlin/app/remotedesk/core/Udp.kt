package app.remotedesk.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

internal actual suspend fun sendUdp(packet: ByteArray, hosts: List<String>, ports: List<Int>): Int = withContext(Dispatchers.IO) {
    var sent = 0
    DatagramSocket().use { socket ->
        socket.broadcast = true
        for (host in hosts) {
            val address = try {
                InetAddress.getByName(host)
            } catch (_: IOException) {
                continue
            }
            for (port in ports) {
                try {
                    socket.send(DatagramPacket(packet, packet.size, address, port))
                    sent++
                } catch (_: IOException) {
                    // Например, нет Wi-Fi или сеть запрещает широковещание на этот адрес — пробуем остальные.
                }
            }
        }
    }
    sent
}
