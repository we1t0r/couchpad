using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>
/// Установка и удаление Couchpad. Установщик — эта же программа: файл Couchpad-Setup.exe (или ключ --setup)
/// показывает окно установки. Всё, что требует прав администратора, делает копия с ключом --install-to
/// (окно UAC подтверждает человек): копирует программу, разрешает её в брандмауэре для домашней сети
/// (чтобы Windows не спрашивала), ставит ярлык в «Пуск» и строку в «Приложения». Автозапуск и всё,
/// что в профиле пользователя, — без прав администратора, в обычной копии.
/// </summary>
static class Installer
{
    public const string ExeName = "Couchpad.exe";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Couchpad";
    const string FirewallRule = "Couchpad";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Couchpad");

    /// <summary>Куда установлен Couchpad (из строки в «Приложениях»); null — не установлен.</summary>
    public static string? InstalledDir
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }
    }

    /// <summary>Эта копия — установленная (а не установщик и не сборка для разработки).</summary>
    public static bool IsInstalledCopy => InstalledDir is { } dir
        && string.Equals(Path.GetDirectoryName(Environment.ProcessPath), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Запуск как установщик: имя файла с «setup» или ключ --setup.</summary>
    public static bool IsSetup(string[] args) =>
        args.Contains("--setup") || Path.GetFileName(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Режимы установщика. true — программа отработала как установщик, <paramref name="code"/> — код выхода.</summary>
    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        int? parent = int.TryParse(Arg("--parent"), out var pid) ? pid : null;

        if (Arg("--install-to") is { } dir)
        {
            code = InstallElevated(dir, args.Contains("--desktop"), args.Contains("--signin"),
                Arg("--data-dir") ?? AgentOptions.DefaultDataDir, int.TryParse(Arg("--port"), out var p) ? p : Protocol.DefaultPort, parent);
            return true;
        }
        if (Arg("--uninstall-elevated") is { } installed)
        {
            code = UninstallElevated(installed, Arg("--purge"), parent);
            return true;
        }
        if (args.Contains("--uninstall"))
        {
            ApplicationConfiguration.Initialize();
            code = Uninstall();
            return true;
        }
        if (IsSetup(args))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new SetupForm());
            return true;
        }
        return false;
    }

    /// <summary>Шаги установки, которым нужны права администратора. Ошибка — окно с текстом и код 1.</summary>
    static int InstallElevated(string dir, bool desktop, bool signIn, string dataDir, int port, int? parent)
    {
        try
        {
            dir = WithAppFolder(Path.GetFullPath(dir));
            var exe = Path.Combine(dir, ExeName);
            var serviceWanted = signIn || SignInSetup.IsInstalled;
            // Чистим только свою папку: в ней уже лежит Couchpad (или прежний RemoteDesk). Чужую непустую не трогаем.
            var ours = File.Exists(exe) || File.Exists(Path.Combine(dir, "RemoteDesk.exe"));
            if (Directory.Exists(dir) && !ours && Directory.EnumerateFileSystemEntries(dir).Any())
                throw new InvalidOperationException($"Папка «{dir}» не пустая. Выберите другую.");
            StopRunning(parent);

            Directory.CreateDirectory(dir);
            Protect(dir);
            foreach (var old in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(old)) Directory.Delete(old, true);
                else File.Delete(old);
            }
            CopyProgram(dir);

            Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", check: false);
            Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow program=\"{exe}\" enable=yes profile=private");

            var programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            Shortcut(Path.Combine(programs, "Couchpad.lnk"), exe);
            var desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Couchpad.lnk");
            if (desktop)
                Shortcut(desktopLink, exe);
            else if (File.Exists(desktopLink))
                File.Delete(desktopLink);

            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Couchpad");
                key.SetValue("DisplayVersion", Protocol.AgentVersion);
                key.SetValue("Publisher", "Couchpad");
                key.SetValue("DisplayIcon", exe);
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                key.SetValue("URLInfoAbout", "https://github.com/");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }

            // Служба входа работает прямо из установленной программы.
            if (serviceWanted && SignInSetup.Install(dataDir, port, exe) != 0)
                return 1;
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("Не удалось установить Couchpad.\n\n" + e.Message, "Couchpad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>
    /// Папку меняет только администратор: оттуда запускается служба от имени системы, обычная программа
    /// не должна подменить её файлы (на дисках вроде D:\ по умолчанию писать могут все).
    /// </summary>
    static void Protect(string dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(security);
    }

    /// <summary>Своя папка всегда называется Couchpad: выбрали «D:\Программы» — ставим в «D:\Программы\Couchpad».</summary>
    public static string WithAppFolder(string dir) =>
        Path.GetFileName(dir.TrimEnd('\\')).Equals("Couchpad", StringComparison.OrdinalIgnoreCase) ? dir.TrimEnd('\\') : Path.Combine(dir, "Couchpad");

    /// <summary>
    /// Себя — в папку установки под именем Couchpad.exe. Обычно установщик — один файл; сборка для разработки
    /// (рядом лежат библиотеки) копируется целиком.
    /// </summary>
    static void CopyProgram(string dir)
    {
        var source = AppContext.BaseDirectory.TrimEnd('\\');
        if (!File.Exists(Path.Combine(source, "Couchpad.dll")))
        {
            File.Copy(Environment.ProcessPath!, Path.Combine(dir, ExeName), overwrite: true);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Закрыть запущенный Couchpad (и прежние сборки RemoteDesk) и службу входа — иначе файлы не заменить.</summary>
    static void StopRunning(int? keep)
    {
        SignInSetup.StopService();
        foreach (var name in new[] { "Couchpad", "RemoteDesk" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        // Не себя и не окно установки или удаления, из которого нас запустили.
                        if (process.Id == Environment.ProcessId || process.Id == keep)
                            continue;
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                    {
                    }
                }
    }

    static void Shortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "Пульт для компьютера с телефона";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Установить с правами администратора и настроить то, что в профиле пользователя. Вызывает окно установки.</summary>
    public static async Task<(bool Ok, string? Error)> InstallAsync(string dir, bool autostart, bool desktop, bool signIn)
    {
        var dataDir = AgentOptions.DefaultDataDir;
        var arguments = $"--install-to \"{dir.TrimEnd('\\')}\" --data-dir \"{dataDir}\" --port {Protocol.DefaultPort} --parent {Environment.ProcessId}"
                        + (desktop ? " --desktop" : "") + (signIn ? " --signin" : "");
        var (started, code) = await RunElevatedAsync(arguments);
        if (!started)
            return (false, "Установка отменена: без разрешения администратора Couchpad не установить.");
        if (code != 0)
            return (false, null); // текст ошибки показала копия с правами администратора

        var exe = Path.Combine(WithAppFolder(dir), ExeName);
        Autostart.SetFor(exe, autostart);
        // Решение об автозапуске принято здесь — первый запуск программы его не меняет.
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "autostart-set"), "");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
        return (true, null);
    }

    /// <summary><c>--uninstall</c>: спросить, убрать своё из профиля пользователя и запустить удаление с правами администратора.</summary>
    static int Uninstall()
    {
        var dir = InstalledDir ?? Path.GetDirectoryName(Environment.ProcessPath)!;
        var purge = new TaskDialogVerificationCheckBox("Удалить и настройки: сопряжённые телефоны, папки, историю");
        var page = new TaskDialogPage
        {
            Caption = "Couchpad",
            Heading = "Удалить Couchpad с этого компьютера?",
            Text = "Телефоны больше не смогут подключаться к этому компьютеру. Приложение на телефоне останется — его удаляют там.",
            Icon = TaskDialogIcon.Warning,
            Verification = purge,
            Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
            DefaultButton = TaskDialogButton.No,
        };
        if (TaskDialog.ShowDialog(page) != TaskDialogButton.Yes)
            return 1;

        var dataDir = AgentOptions.DefaultDataDir;
        var arguments = $"--uninstall-elevated \"{dir.TrimEnd('\\')}\" --parent {Environment.ProcessId}" + (purge.Checked ? $" --purge \"{dataDir}\"" : "");
        var (started, code) = RunElevatedAsync(arguments).GetAwaiter().GetResult();
        if (!started || code != 0)
            return 1;
        Autostart.Remove();
        SendToPhone.Unregister();
        MessageBox.Show("Couchpad удалён.", "Couchpad", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static int UninstallElevated(string dir, string? purgeDataDir, int? parent)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            StopRunning(parent);
            SignInSetup.Uninstall(deleteProgram: false);
            Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", check: false);
            foreach (var link in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Couchpad.lnk"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Couchpad.lnk"),
                     })
                if (File.Exists(link))
                    File.Delete(link);
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            if (purgeDataDir is not null && Directory.Exists(purgeDataDir))
                Directory.Delete(purgeDataDir, true);

            // Себя не удалить, пока работаешь: папку убирает PowerShell через пару секунд, когда программа закроется.
            // Удаляем только свою папку (…\Couchpad с Couchpad.exe внутри).
            if (Path.GetFileName(dir).Equals("Couchpad", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, ExeName)))
            {
                var quoted = dir.Replace("'", "''");
                Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -WindowStyle Hidden -Command \"for ($i = 0; $i -lt 30; $i++) { Start-Sleep 1; "
                    + $"Remove-Item -LiteralPath '{quoted}' -Recurse -Force -ErrorAction SilentlyContinue; "
                    + $"if (-not (Test-Path -LiteralPath '{quoted}')) {{ break }} }}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.Dispose();
            }
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("Не удалось удалить Couchpad.\n\n" + e.Message, "Couchpad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Запустить эту же программу с правами администратора. Started=false — человек отказался в окне UAC.</summary>
    static async Task<(bool Started, int ExitCode)> RunElevatedAsync(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
            })!;
            await process.WaitForExitAsync();
            return (true, process.ExitCode);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return (false, 0);
        }
    }

    static void Run(string exe, string arguments, bool check = true)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        if (check && process.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {arguments.Split(' ')[0]}: код {process.ExitCode}");
    }
}
