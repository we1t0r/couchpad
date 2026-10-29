using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesk.Agent.Core;

/// <summary>Питание и мониторы Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPower : IPowerSink, IDisposable
{
    const int WmSysCommand = 0x0112;
    const int ScMonitorPower = 0xF170;
    const int MonitorOff = 2, MonitorOn = -1;
    static readonly nint HwndBroadcast = 0xFFFF;

    readonly MonitorGuard guard = new();

    public void Execute(PowerAction action, bool force, bool keepMonitorsOff)
    {
        switch (action)
        {
            case PowerAction.MonitorsOff:
                MonitorsOff();
                guard.Start(keepMonitorsOff, MonitorsOff);
                break;
            case PowerAction.MonitorsOn:
                guard.Stop();
                MonitorsOn();
                break;
            case PowerAction.Lock:
                if (!LockWorkStation())
                    throw new Win32Exception();
                break;
            case PowerAction.Sleep:
            case PowerAction.Hibernate:
                var hibernate = action == PowerAction.Hibernate;
                if (!SetSuspendState(hibernate, false, false))
                    throw new InvalidOperationException(hibernate
                        ? "Гибернация выключена в Windows. Включить: командная строка от администратора → powercfg /hibernate on"
                        : "Windows не дала перевести компьютер в сон: " + new Win32Exception().Message);
                break;
            case PowerAction.Shutdown:
                Shutdown(force ? "/s /f /t 0" : "/s /t 0");
                break;
            case PowerAction.Restart:
                Shutdown(force ? "/r /f /t 0" : "/r /t 0");
                break;
            case PowerAction.SignOut:
                Shutdown(force ? "/l /f" : "/l");
                break;
        }
    }

    static void MonitorsOff() =>
        // PostMessage, а не SendMessage: зависшее окно не задержит агента.
        PostMessageW(HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorOff);

    static void MonitorsOn()
    {
        PostMessageW(HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorOn);
        // На многих ПК SC_MONITORPOWER «вкл» не срабатывает — будим нулевым движением мыши, как будто её тронули.
        var input = new MouseMoveInput { type = 0 };
        input.mi.dwFlags = 0x0001;
        SendInput(1, [input], Marshal.SizeOf<MouseMoveInput>());
    }

    static void Shutdown(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("Не удалось запустить shutdown.exe");
        if (!p.WaitForExit(5000))
            return;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Windows отказалась (код {p.ExitCode}). Возможно, выключение запрещено политикой.");
    }

    public void Dispose() => guard.Dispose();

    [DllImport("user32.dll")]
    static extern bool PostMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    static extern uint SendInput(uint count, MouseMoveInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct MouseMoveInput
    {
        public uint type;
        public MouseData mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseData
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }
}

/// <summary>
/// «Будить мониторы только кнопкой из приложения»: пока режим включён, любое движение мыши
/// (своей или с телефона) снова гасит мониторы. Нажатие клавиши на клавиатуре ПК режим снимает —
/// чтобы человек за компьютером никогда не остался без экрана.
/// Перехватчики ввода живут в своём потоке с очередью сообщений и ничего не блокируют.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class MonitorGuard : IDisposable
{
    const int WhKeyboardLl = 13, WhMouseLl = 14;
    const int WmQuit = 0x0012, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmMouseMove = 0x0200;
    const int LlkhfInjected = 0x10;

    readonly Lock gate = new();
    Thread? thread;
    uint threadId;
    volatile bool active;
    Action? turnOff;
    long lastReoff;

    // Делегаты держим в полях, иначе сборщик мусора уберёт их, пока Windows их вызывает.
    HookProc? keyboardProc, mouseProc;

    public void Start(bool keep, Action off)
    {
        lock (gate)
        {
            if (!keep)
            {
                StopLocked();
                return;
            }
            turnOff = off;
            active = true;
            if (thread is not null)
                return;
            var ready = new ManualResetEventSlim();
            thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "RemoteDesk monitor guard" };
            thread.Start();
            ready.Wait(2000);
        }
    }

    public void Stop()
    {
        lock (gate)
            StopLocked();
    }

    void StopLocked()
    {
        active = false;
        if (thread is null)
            return;
        PostThreadMessageW(threadId, WmQuit, 0, 0);
        thread.Join(2000);
        thread = null;
    }

    void Run(ManualResetEventSlim ready)
    {
        threadId = GetCurrentThreadId();
        keyboardProc = OnKeyboard;
        mouseProc = OnMouse;
        var module = GetModuleHandleW(null);
        var kb = SetWindowsHookExW(WhKeyboardLl, keyboardProc, module, 0);
        var mouse = SetWindowsHookExW(WhMouseLl, mouseProc, module, 0);
        ready.Set();
        try
        {
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        finally
        {
            if (kb != 0) UnhookWindowsHookEx(kb);
            if (mouse != 0) UnhookWindowsHookEx(mouse);
        }
    }

    nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && active && wParam is WmKeyDown or WmSysKeyDown)
        {
            var flags = Marshal.ReadInt32(lParam, 8);
            // Настоящая клавиатура ПК — человек за компьютером: отдаём ему экран.
            if ((flags & LlkhfInjected) == 0)
                active = false;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && active && wParam == WmMouseMove)
        {
            // Не чаще раза в полсекунды и не в потоке перехватчика.
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastReoff) > 500)
            {
                Interlocked.Exchange(ref lastReoff, now);
                var off = turnOff;
                if (off is not null)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(300);
                        if (active)
                            off();
                    });
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose() => Stop();

    delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowsHookExW(int idHook, HookProc proc, nint hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out Msg msg, nint hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    static extern nint DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessageW(uint threadId, int msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string? name);
}
