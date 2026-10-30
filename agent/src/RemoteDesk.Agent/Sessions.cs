using System.Runtime.InteropServices;

namespace RemoteDesk.Agent;

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
            // WTSINFOEXW: Level, затем WTSINFOEX_LEVEL1_W { SessionId, SessionState, SessionFlags, ... }.
            return bytes >= 16 && Marshal.ReadInt32(buffer, 0) == 1 && Marshal.ReadInt32(buffer, 12) == SessionStateLock;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Проверка не чаще раза в 300 мс: её спрашивают на каждое движение мыши.</summary>
    public static Func<bool> Cached(Func<bool> check)
    {
        var gate = new Lock();
        long at = long.MinValue;
        var value = false;
        return () =>
        {
            lock (gate)
            {
                if (Environment.TickCount64 - at > 300)
                {
                    value = check();
                    at = Environment.TickCount64;
                }
                return value;
            }
        };
    }

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformationW(nint server, uint session, int infoClass, out nint buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(nint memory);
}
