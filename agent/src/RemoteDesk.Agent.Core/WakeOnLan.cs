using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace RemoteDesk.Agent.Core;

public enum WolState { Ok, Warning, Problem, Unknown }

/// <summary>Один пункт проверки: понятный заголовок и что делать, если не в порядке.</summary>
public sealed record WolCheck(string Id, WolState State, string Title, string? Hint);

/// <param name="CanFix">Есть что исправить автоматически (нужны права администратора на ПК).</param>
public sealed record WolReport(IReadOnlyList<WolCheck> Checks, bool CanFix)
{
    /// <summary>Всё, что можно проверить из Windows, в порядке (BIOS проверить нельзя).</summary>
    public bool Ready => Checks.All(c => c.State is WolState.Ok or WolState.Unknown);
}

/// <summary>Проверка и настройка «Включения по сети» на этом ПК.</summary>
public interface IWakeOnLanSetup
{
    WolReport Check();

    /// <summary>Исправить, что можно. Windows спросит разрешение администратора. false — пользователь отказал.</summary>
    Task<bool> FixAsync();
}

public sealed class NullWakeOnLan : IWakeOnLanSetup
{
    public WolReport Check() => new([new WolCheck("os", WolState.Unknown, "Проверка доступна только в Windows", null)], false);
    public Task<bool> FixAsync() => Task.FromResult(false);
}

/// <summary>
/// Читает настройки без прав администратора: реестр сетевой карты, «Быстрый запуск», powercfg.
/// Исправляет одним скриптом PowerShell с запросом прав (UAC) — пользователь видит, что разрешает.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWakeOnLan : IWakeOnLanSetup
{
    const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    sealed record Adapter(string Description, string? MagicPacket, string? ShutdownWake);

    public WolReport Check()
    {
        var checks = new List<WolCheck>();
        var nic = WiredNic();
        if (nic is null)
        {
            checks.Add(new("cable", WolState.Problem, "Компьютер не подключён к сети кабелем",
                "Включение по сети почти всегда работает только по проводу. Подключите ПК к роутеру кабелем Ethernet."));
            checks.Add(Bios());
            return new WolReport(checks, false);
        }

        checks.Add(new("cable", WolState.Ok, $"Подключён кабелем: {nic.Name}", null));
        var adapter = ReadAdapter(nic.Id);
        var canFix = false;

        if (adapter?.MagicPacket == "1")
        {
            checks.Add(new("magic", WolState.Ok, "Сетевая карта просыпается от «магического пакета»", null));
        }
        else
        {
            canFix = true;
            checks.Add(new("magic", WolState.Problem, "Пробуждение по «магическому пакету» выключено в сетевой карте",
                "Нажмите «Исправить» — Couchpad включит его сам."));
        }

        if (adapter?.ShutdownWake is { } s5)
        {
            if (s5 == "1")
                checks.Add(new("shutdown", WolState.Ok, "Карта ждёт пакет и после выключения ПК", null));
            else
            {
                canFix = true;
                checks.Add(new("shutdown", WolState.Problem, "Карта не слушает сеть, когда ПК выключен",
                    "Нажмите «Исправить» — Couchpad включит «Включение по локальной сети после отключения»."));
            }
        }

        var armed = adapter is not null && WakeArmed().Any(d => d.Equals(adapter.Description, StringComparison.OrdinalIgnoreCase));
        if (armed)
            checks.Add(new("armed", WolState.Ok, "Windows разрешает сетевой карте будить компьютер", null));
        else
        {
            canFix = true;
            checks.Add(new("armed", WolState.Problem, "Windows не разрешает сетевой карте будить компьютер",
                "Нажмите «Исправить» — Couchpad поставит галочку «Разрешить этому устройству выводить компьютер из ждущего режима»."));
        }

        if (FastStartup())
        {
            canFix = true;
            checks.Add(new("faststartup", WolState.Warning, "Включён «Быстрый запуск» Windows",
                "Из-за него после выключения ПК сетевая карта часто засыпает и не слышит пакет. Нажмите «Исправить», чтобы отключить. Сон и перезагрузка не пострадают; холодный старт станет на пару секунд дольше."));
        }
        else
        {
            checks.Add(new("faststartup", WolState.Ok, "«Быстрый запуск» не мешает", null));
        }

        checks.Add(Bios());
        return new WolReport(checks, canFix);
    }

    public async Task<bool> FixAsync()
    {
        var nic = WiredNic();
        var adapter = nic is null ? null : ReadAdapter(nic.Id);
        if (adapter is null)
            return false;

        var desc = adapter.Description.Replace("'", "''");
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $a = Get-NetAdapter -InterfaceDescription '{{desc}}'
            Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1
            Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword 'S5WakeOnLan' -RegistryValue 1
            Set-NetAdapterPowerManagement -Name $a.Name -WakeOnMagicPacket Enabled
            powercfg /deviceenablewake '{{desc}}'
            reg add 'HKLM\{{PowerKey}}' /v HiberbootEnabled /t REG_DWORD /d 0 /f | Out-Null
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null)
                return false;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Пользователь нажал «Нет» в окне контроля учётных записей.
            return false;
        }
        catch (TimeoutException)
        {
            return true;
        }
    }

    static NetworkInterface? WiredNic() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                        or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
                    && !n.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                    && !n.Description.Contains("npcap", StringComparison.OrdinalIgnoreCase)
                    && !n.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));

    /// <summary>Настройки драйвера сетевой карты из реестра (читать можно без прав администратора).</summary>
    static Adapter? ReadAdapter(string interfaceId)
    {
        using var root = Registry.LocalMachine.OpenSubKey(NetClass);
        if (root is null)
            return null;
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("NetCfgInstanceId") is string id && id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase))
                    return new Adapter(key.GetValue("DriverDesc") as string ?? "", key.GetValue("*WakeOnMagicPacket") as string,
                        key.GetValue("S5WakeOnLan") as string);
            }
            catch (System.Security.SecurityException)
            {
                // Служебные подразделы закрыты — пропускаем.
            }
        }
        return null;
    }

    /// <summary>Устройства, которым разрешено будить ПК (powercfg /devicequery wake_armed).</summary>
    static IReadOnlyList<string> WakeArmed()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", "/devicequery wake_armed")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            });
            if (p is null)
                return [];
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    static bool FastStartup()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PowerKey);
        return key?.GetValue("HiberbootEnabled") is int v && v != 0;
    }

    /// <summary>BIOS из Windows не проверить — даём инструкцию под производителя платы.</summary>
    static WolCheck Bios()
    {
        string maker = "", product = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            maker = key?.GetValue("BaseBoardManufacturer") as string ?? "";
            product = key?.GetValue("BaseBoardProduct") as string ?? "";
        }
        catch (System.Security.SecurityException)
        {
        }

        var m = maker.ToLowerInvariant();
        var how = m switch
        {
            _ when m.Contains("micro-star") || m.Contains("msi") =>
                "Settings → Advanced → Wake Up Event Setup → «Resume By PCI-E Device» = Enabled. И Settings → Advanced → Power Management Setup → «ErP Ready» = Disabled.",
            _ when m.Contains("asus") =>
                "Advanced → APM Configuration → «Power On By PCI-E» = Enabled, «ErP Ready» = Disabled.",
            _ when m.Contains("gigabyte") =>
                "Settings → Platform Power → «Wake on LAN» = Enabled, «ErP» = Disabled.",
            _ when m.Contains("asrock") =>
                "Advanced → ACPI Configuration → «PCIE Devices Power On» = Enabled. Если есть «Deep Sleep» — Disabled.",
            _ =>
                "Найдите пункт вроде «Wake on LAN», «Power On By PCI-E» или «Resume by PCI-E» и включите его. «ErP» или «Deep Sleep» выключите.",
        };
        var board = string.IsNullOrWhiteSpace(product) ? "" : $" ({maker.Trim()} {product.Trim()})";
        return new WolCheck("bios", WolState.Unknown, $"Настройка в BIOS{board} — проверить из Windows нельзя",
            "При включении ПК нажмите Delete (или F2), чтобы открыть BIOS. " + how + " Сохраните (F10).");
    }
}
