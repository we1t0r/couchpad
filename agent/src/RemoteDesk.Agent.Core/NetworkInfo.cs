using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesk.Agent.Core;

/// <summary>Адреса ПК в локальных сетях и MAC-адреса сетевых карт (для Wake-on-LAN).</summary>
public static class NetworkInfo
{
    // Виртуальные адаптеры (VirtualBox, Hyper-V, VPN) телефону не нужны: через них он ПК не увидит.
    static readonly string[] VirtualMarkers =
        ["virtual", "vmware", "hyper-v", "vbox", "npcap", "loopback", "tap-", "wireguard", "tailscale", "zerotier", "wintun", "bluetooth"];

    public static IReadOnlyList<MacInfo> Interfaces()
    {
        var result = new List<MacInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || VirtualMarkers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase)
                                           || nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
                continue;

            var wired = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;
            var mac = FormatMac(nic.GetPhysicalAddress());

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || IsLinkLocal(ip))
                    continue;
                result.Add(new MacInfo(mac, nic.Name, ip.ToString(), wired, Broadcast(ip, address.IPv4Mask)));
            }
        }
        // Проводные сначала: по кабелю связь надёжнее, и только через кабель обычно работает WoL.
        return result.OrderByDescending(m => m.Wired).ToList();
    }

    public static IReadOnlyList<string> Addresses() => Interfaces().Select(i => i.Ip).Distinct().ToList();

    static bool IsLinkLocal(IPAddress ip) => ip.GetAddressBytes() is [169, 254, ..];

    /// <summary>
    /// Адрес из домашней сети (или Tailscale, 100.64.0.0/10), а не из интернета. Через проброс порта
    /// на роутере телефон приходит со своим адресом в интернете.
    /// </summary>
    public static bool IsLocal(IPAddress? ip)
    {
        if (ip is null)
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
        return ip.GetAddressBytes() switch
        {
            [10, ..] or [192, 168, ..] or [169, 254, ..] => true,
            [172, var b, ..] => b is >= 16 and <= 31,
            [100, var b, ..] => b is >= 64 and <= 127,
            _ => false,
        };
    }

    /// <summary>Широковещательный адрес подсети (192.168.0.255): туда телефон шлёт пакет пробуждения.</summary>
    internal static string? Broadcast(IPAddress ip, IPAddress? mask)
    {
        if (mask is null || mask.Equals(IPAddress.Any))
            return null;
        var a = ip.GetAddressBytes();
        var m = mask.GetAddressBytes();
        return new IPAddress(a.Zip(m, (x, y) => (byte)(x | ~y)).ToArray()).ToString();
    }

    static string FormatMac(PhysicalAddress address) =>
        string.Join(':', address.GetAddressBytes().Select(b => b.ToString("X2")));

    public static string OsDescription()
    {
        if (!OperatingSystem.IsWindows())
            return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var build = Environment.OSVersion.Version.Build;
        return $"Windows {(build >= 22000 ? "11" : "10")} (сборка {build})";
    }
}
