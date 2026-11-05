using Microsoft.Win32;

namespace Couchpad.Agent;

/// <summary>
/// Запуск вместе с Windows: строка в HKCU\…\Run для текущего пользователя (без прав администратора).
/// Агент стартует после входа в Windows. При первом запуске включается сам; если человек снимет
/// галочку в меню, больше не включается. Установщик решает за первый запуск сам (галочка в его окне).
/// </summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Couchpad";

    static string Command(string exe) => $"\"{exe}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled) => SetFor(Environment.ProcessPath!, enabled);

    public static void SetFor(string exe, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command(exe));
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Удаление Couchpad.</summary>
    public static void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Первый запуск — включить автозапуск. Отметка в папке данных: решение человека потом не трогаем.</summary>
    public static void EnableOnFirstRun(string dataDir)
    {
        var marker = Path.Combine(dataDir, "autostart-set");
        try
        {
            if (File.Exists(marker))
                return;
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
