using System.Runtime.InteropServices;

namespace Couchpad.Agent;

/// <summary>Сеансы Windows: кто вошёл и заблокирован ли компьютер.</summary>
static class Sessions
{
    const int WtsUserName = 5, WtsSessionInfoEx = 25, SessionStateLock = 0;

    /// <summary>Сеанс за монитором и клавиатурой ПК; null — сейчас никакого (идёт смена пользователя).</summary>
    public static int? Console => WTSGetActiveConsoleSessionId() is var id && id != uint.MaxValue ? (int)id : null;

    public static int Current { get; } = ProcessIdToSessionId((uint)Environment.ProcessId, out var id) ? (int)id : 0;

    /// <summary>В сеансе кто-то вошёл в Windows.</summary>
    public static bool HasUser(int session)
    {
        if (!WTSQuerySessionInformationW(0, (uint)session, WtsUserName, out var buffer, out _))
            return false;
        try
        {
            return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Сеанс заблокирован (экран блокировки).</summary>
    public static bool IsLocked(int session)
    {
        if (!WTSQuerySessionInformationW(0, (uint)session, WtsSessionInfoEx, out var buffer, out var bytes))
            return false;
        try
        {
            // WTSINFOEXW: Level (4 байта), выравнивание до 8 (в структуре есть LARGE_INTEGER), затем
            // WTSINFOEX_LEVEL1_W { SessionId @8, SessionState @12, SessionFlags @16, ... }.
            return bytes >= 20 && Marshal.ReadInt32(buffer, 0) == 1 && Marshal.ReadInt32(buffer, 16) == SessionStateLock;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Когда в этот сеанс вошли в Windows (UTC); null — никто не вошёл или узнать не удалось.</summary>
    public static DateTime? LogonTimeUtc(int session)
    {
        if (!WTSQuerySessionInformationW(0, (uint)session, WtsSessionInfoEx, out var buffer, out var bytes))
            return null;
        try
        {
            // WTSINFOEX_LEVEL1_W: … SessionFlags @16, WinStationName[33] @20, UserName[21] @86, DomainName[18] @128, LogonTime @168.
            if (bytes < 176 || Marshal.ReadInt32(buffer, 0) != 1 || Marshal.ReadInt64(buffer, 168) is not (> 0 and var time))
                return null;
            return DateTime.FromFileTimeUtc(time);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Windows сейчас завершает этот сеанс (выключение, перезагрузка, выход).</summary>
    public static bool ShuttingDown => GetSystemMetrics(0x2000 /* SM_SHUTTINGDOWN */) != 0;

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Ввод сейчас идёт на обычный рабочий стол пользователя («Default»). Нет — значит, на экране защищённый стол
    /// Winlogon: окно UAC, блокировка или вход. Обычной программе Windows его даже открыть не даёт, а помощнику
    /// службы (система) — даёт, и тот видит его имя.
    /// </summary>
    public static bool OnUserDesktop()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0)
            return false;
        try
        {
            var name = new char[64];
            return GetUserObjectInformationW(desktop, UoiName, name, name.Length * 2, out _)
                   && new string(name).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    const uint DesktopReadObjects = 0x0001;
    const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(nint handle, int index, [Out] char[] info, int length, out int needed);

    [DllImport("user32.dll")]
    static extern bool CloseDesktop(nint desktop);

    /// <summary>Проверка не чаще раза в 300 мс: её спрашивают на каждое движение мыши.</summary>
    public static Func<bool> Cached(Func<bool> check) => Core.CachedCheck.Create(check);

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformationW(nint server, uint session, int infoClass, out nint buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(nint memory);
}
