using Microsoft.Win32;

namespace RemoteDesk.Agent;

/// <summary>
/// Запуск вместе с Windows: строка в HKCU\…\Run для текущего пользователя (без прав администратора).
/// Агент стартует после входа в Windows. При первом запуске включается сам; если человек снимет
/// галочку в меню, больше не включается.
/// </summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RemoteDesk";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Первый запуск — включить автозапуск. Отметка в папке данных: решение человека потом не трогаем.</summary>
    public static void EnableOnFirstRun(string dataDir)
    {
        var marker = Path.Combine(dataDir, "autostart-set");
        if (File.Exists(marker))
            return;
        try
        {
            Set(true);
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(marker, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Не вышло — человек включит в меню сам.
        }
    }
}
