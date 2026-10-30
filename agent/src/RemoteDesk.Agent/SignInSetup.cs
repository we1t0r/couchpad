using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace RemoteDesk.Agent;

/// <summary>
/// Установка службы входа. Всё делает копия агента с правами администратора (окно UAC подтверждает пользователь):
/// копирует программу в Program Files (оттуда её не подменит обычная программа — служба работает от имени системы),
/// регистрирует службу, разрешает её в брандмауэре для домашней сети и запускает.
/// </summary>
static class SignInSetup
{
    const string FirewallRule = "Couchpad: вход в Windows";

    // Сборка для разработки (не установленная) копирует себя сюда; установленная программа служит службе сама.
    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Couchpad");
    static string InstalledExe => Path.Combine(InstallDir, Path.GetFileName(Environment.ProcessPath!));

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{SignInService.Name}");
            return key is not null;
        }
    }

    /// <summary>Служба установлена из другой сборки агента — её нужно обновить.</summary>
    public static bool IsOutdated => IsInstalled && !Installer.IsInstalledCopy && Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Any(file =>
    {
        var installed = Path.Combine(InstallDir, Path.GetFileName(file));
        return !File.Exists(installed) || File.GetLastWriteTimeUtc(installed) != File.GetLastWriteTimeUtc(file);
    });

    /// <summary>Запустить установку (или удаление) с правами администратора. false — пользователь отказался в окне UAC.</summary>
    public static async Task<(bool Started, int ExitCode)> RunElevatedAsync(bool install, string dataDir, int port)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = install
                ? $"--install-service --data-dir \"{dataDir}\" --port {port}"
                : "--uninstall-service",
        };
        try
        {
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            return (true, process.ExitCode);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return (false, 0);
        }
    }

    /// <summary><c>--install-service</c> (уже с правами администратора).</summary>
    public static int Install(string dataDir, int port, string? exe = null)
    {
        try
        {
            StopService();
            if (exe is null && Installer.IsInstalledCopy)
                exe = Environment.ProcessPath!;
            if (exe is null)
            {
                CopyProgram();
                exe = InstalledExe;
            }
            var binPath = $"\"{exe}\" --service --data-dir \"{dataDir}\" --port {port}";
            if (IsInstalled)
                Sc($"config {SignInService.Name} binPath= {ScQuote(binPath)} start= auto");
            else
                Sc($"create {SignInService.Name} binPath= {ScQuote(binPath)} start= auto obj= LocalSystem DisplayName= \"Couchpad: вход в Windows\"");
            Sc($"description {SignInService.Name} \"Показывает экран входа и блокировки Windows на телефоне с Couchpad, чтобы ввести PIN или пароль.\"");
            // Упала — перезапустить через 5 секунд.
            Sc($"failure {SignInService.Name} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
            Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", check: false);
            Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow program=\"{exe}\" enable=yes profile=private");
            Sc($"start {SignInService.Name}");
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("Не удалось включить вход в Windows с телефона.\n\n" + e.Message, "Couchpad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary><c>--uninstall-service</c> (уже с правами администратора).</summary>
    public static int Uninstall(bool deleteProgram = true)
    {
        try
        {
            StopService();
            if (IsInstalled)
                Sc($"delete {SignInService.Name}");
            Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", check: false);
            // Копию для службы удаляем, только если это она, а не установленный Couchpad.
            if (string.Equals(Installer.InstalledDir?.TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase))
                deleteProgram = false;
            for (var attempt = 0; deleteProgram && attempt < 10 && Directory.Exists(InstallDir); attempt++)
            {
                try
                {
                    Directory.Delete(InstallDir, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("Не удалось выключить вход в Windows с телефона.\n\n" + e.Message, "Couchpad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static void StopService()
    {
        if (!IsInstalled)
            return;
        Run("sc.exe", $"stop {SignInService.Name}", check: false);
        // Ждём, пока служба и помощник закроются: иначе их файлы не перезаписать.
        for (var i = 0; i < 30; i++)
        {
            if (Run("sc.exe", $"query {SignInService.Name}", check: false).Contains("STOPPED"))
                break;
            Thread.Sleep(500);
        }
        // Помощника служба закрывает сама. Добиваем копию для службы, только если она отдельная:
        // у установленного Couchpad служба и агент пользователя запущены из одного файла.
        if (Installer.IsInstalledCopy || Installer.InstalledDir is not null)
            return;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledExe)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                }
            }
        }
    }

    static void CopyProgram()
    {
        var source = AppContext.BaseDirectory.TrimEnd('\\');
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(InstallDir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    /// <summary>sc.exe и netsh пишут в кодировке консоли (866 для русской Windows).</summary>
    static System.Text.Encoding OemEncoding()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    /// <summary>sc.exe разбирает значение в кавычках, а кавычки внутри — через \".</summary>
    static string ScQuote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    static void Sc(string arguments) => Run("sc.exe", arguments);

    static string Run(string exe, string arguments, bool check = true)
    {
        var start = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = OemEncoding(),
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (check && process.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {arguments.Split(' ')[0]}: {output.Trim()}");
        return output;
    }
}
