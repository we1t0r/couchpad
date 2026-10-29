using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesk.Agent.Core;

/// <summary>Питание и мониторы Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPower : IPowerSink, IDisposable
{
    readonly Display display = new();

    /// <summary>Мониторы не удалось удержать выключенными: их снова и снова будит программа.</summary>
    public event Action<string>? MonitorsProblem
    {
        add => display.GaveUp += value;
        remove => display.GaveUp -= value;
    }

    public void Execute(PowerAction action, bool force, bool keepMonitorsOff)
    {
        switch (action)
        {
            case PowerAction.MonitorsOff:
                display.Off(keepMonitorsOff);
                break;
            case PowerAction.MonitorsOn:
                display.On();
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

    static void Shutdown(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("Не удалось запустить shutdown.exe");
        if (!p.WaitForExit(5000))
            return;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Windows отказалась (код {p.ExitCode}). Возможно, выключение запрещено политикой.");
    }

    public void Dispose() => display.Dispose();

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}

/// <summary>
/// Мониторы через собственное скрытое окно в отдельном потоке.
///
/// Команда «погасить» уходит только этому окну, а не всем окнам системы: рассылка всем задевала
/// игры и программы и долго «разгребалась» после включения. Перехватчиков мыши и клавиатуры нет —
/// они пропускали через агента каждое событие мыши и вызывали лаги в играх.
///
/// Windows сообщает окну, что дисплей включился. Если с момента выключения не было ни мыши, ни
/// клавиатуры (GetLastInputInfo — просто время последнего ввода, без перехвата), дисплей разбудила
/// программа (например, запущенная игра). Windows такой программе не перечить, поэтому мониторы
/// усыпляются командой самому монитору по DDC/CI: для Windows и игры экран остаётся включённым,
/// будить нечего. Просыпаются они по кнопке из приложения или (обычный режим) от мыши, клавиатуры,
/// тачпада телефона — это видно по GetLastInputInfo, который окно опрашивает раз в 250 мс.
/// Если монитор DDC/CI не понимает, дисплей гасится снова обычной командой.
///
/// Режим «будить только кнопкой из приложения»: дисплей гасится снова и после мыши или телефона.
/// Снимает режим только клавиша на клавиатуре ПК — это человек за компьютером. О клавишах окно
/// узнаёт через Raw Input только клавиатуры, и только пока режим включён; мышь не затрагивается.
///
/// Если дисплей просыпается снова и снова (мышь сама «дрожит», программа не отпускает экран),
/// попытки прекращаются, чтобы не мигать экраном.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class Display : IDisposable
{
    const uint WmDestroy = 0x0002, WmClose = 0x0010, WmTimer = 0x0113, WmSysCommand = 0x0112, WmInput = 0x00FF;
    const uint WmPowerBroadcast = 0x0218, PbtPowerSettingChange = 0x8013, WmApp = 0x8000;
    const int ScMonitorPower = 0xF170;
    const int ReoffTimer = 1, InputTimer = 2;
    static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    static readonly TimeSpan KeyboardWakes = TimeSpan.FromSeconds(3);
    const int MaxReoffs = 3;
    static readonly TimeSpan ReoffWindow = TimeSpan.FromSeconds(30);

    readonly Lock gate = new();
    readonly WndProc proc;
    Thread? thread;
    nint hwnd;

    // Состояние ниже меняется только в потоке окна.
    bool wantOff, keepOff, ddcOff;
    uint inputAtOff;
    long lastKey;
    readonly Queue<long> reoffs = new();

    public Display() => proc = WindowProc;

    /// <summary>Программа не даёт погасить мониторы; текст — для человека. Вызывается из потока окна.</summary>
    public event Action<string>? GaveUp;

    public void Off(bool keep) => Post(1, keep ? 1 : 0);

    public void On() => Post(0, 0);

    void Post(nint wParam, nint lParam)
    {
        lock (gate)
        {
            if (thread is null)
            {
                using var ready = new ManualResetEventSlim();
                thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "RemoteDesk display" };
                thread.Start();
                ready.Wait(3000);
            }
            if (hwnd == 0)
                throw new InvalidOperationException("Не удалось создать служебное окно для управления мониторами");
            PostMessageW(hwnd, WmApp, wParam, lParam);
        }
    }

    void Run(ManualResetEventSlim ready)
    {
        var instance = GetModuleHandleW(null);
        var className = "RemoteDeskDisplay" + Environment.ProcessId;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = instance,
            lpszClassName = className,
        };
        RegisterClassExW(ref wc);
        // Обычное (не message-only) окно верхнего уровня, но невидимое: так SC_MONITORPOWER работает надёжно.
        hwnd = CreateWindowExW(0, className, "RemoteDesk", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, instance, 0);
        var guid = ConsoleDisplayState;
        var notify = hwnd != 0 ? RegisterPowerSettingNotification(hwnd, ref guid, 0) : 0;
        ready.Set();
        if (hwnd == 0)
            return;
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
            if (notify != 0)
                UnregisterPowerSettingNotification(notify);
        }
    }

    nint WindowProc(nint h, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmApp when wParam == 1:
                SetKeepOff(lParam == 1);
                wantOff = true;
                inputAtOff = LastInput();
                reoffs.Clear();
                if (ddcOff)
                    WatchInput(); // уже спят по DDC — меняется только, что их будит
                else
                    TurnOff();
                return 0;
            case WmApp:
                Release();
                KillTimer(hwnd, ReoffTimer);
                TurnOn();
                return 0;
            case WmInput:
                // Пришло событие клавиатуры (только её мы и слушаем) — за компьютером человек.
                lastKey = Environment.TickCount64;
                if (ddcOff && keepOff)
                    Release();
                break;
            case WmTimer when wParam == InputTimer:
                if (ddcOff && !keepOff && (int)(LastInput() - inputAtOff) > 0)
                    Release();
                return 0;
            case WmPowerBroadcast when wParam == PbtPowerSettingChange:
                OnDisplayState(lParam);
                return 1;
            case WmTimer when wParam == ReoffTimer:
                KillTimer(hwnd, ReoffTimer);
                if (wantOff)
                    TurnOff();
                return 0;
            case WmDestroy:
                Release(); // мониторы, уснувшие по DDC, без агента сами не проснутся
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(h, msg, wParam, lParam);
    }

    void OnDisplayState(nint setting)
    {
        if (!wantOff || ddcOff || Marshal.PtrToStructure<Guid>(setting) != ConsoleDisplayState)
            return;
        var state = Marshal.ReadInt32(setting, 20); // 0 — выключен, 1 — включён, 2 — притушен
        if (state != 1)
            return;
        var now = Environment.TickCount64;

        var human = keepOff
            ? now - lastKey < KeyboardWakes.TotalMilliseconds
            : (int)(LastInput() - inputAtOff) > 0; // был ввод после выключения: мышь, клавиатура, телефон
        if (human)
        {
            Release();
            return;
        }
        // Разбудила программа — усыпляем сами мониторы, тогда ей будить нечего.
        if (Ddc.SetPower(Ddc.Standby))
        {
            ddcOff = true;
            WatchInput();
            return;
        }
        while (reoffs.Count > 0 && now - reoffs.Peek() > ReoffWindow.TotalMilliseconds)
            reoffs.Dequeue();
        if (reoffs.Count >= MaxReoffs)
        {
            // Что-то будит дисплей снова и снова — не мигаем экраном, оставляем включённым.
            Release();
            GaveUp?.Invoke(WhoWakes());
            return;
        }
        reoffs.Enqueue(now);
        // Небольшая пауза: Windows только что включила дисплей и сразу же выключить его не даст.
        SetTimer(hwnd, ReoffTimer, 500, 0);
    }

    void Release()
    {
        wantOff = false;
        SetKeepOff(false);
        KillTimer(hwnd, InputTimer);
        if (ddcOff)
        {
            ddcOff = false;
            Ddc.SetPower(Ddc.On);
        }
    }

    /// <summary>Пока мониторы спят по DDC: в обычном режиме их будит любой ввод — следим за временем ввода.</summary>
    void WatchInput()
    {
        if (keepOff)
            KillTimer(hwnd, InputTimer);
        else
            SetTimer(hwnd, InputTimer, 250, 0);
    }

    /// <summary>Время последнего ввода мышью или клавиатурой (в мс от запуска системы).</summary>
    static uint LastInput()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    void TurnOff() => DefWindowProcW(hwnd, WmSysCommand, ScMonitorPower, 2);

    /// <summary>Объяснение для человека: какая программа, скорее всего, будит мониторы.</summary>
    static string WhoWakes()
    {
        string? name = null;
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != 0 && pid != Environment.ProcessId)
            {
                using var p = Process.GetProcessById((int)pid);
                if (!p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    name = p.ProcessName + ".exe";
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
        return name is null
            ? "Мониторы снова и снова включает какая-то программа на компьютере — обычно игра или видео. Закройте её и выключите мониторы ещё раз."
            : $"Мониторы снова и снова включает {name}. Пока программа запущена, погасить их не получится — закройте её и выключите мониторы ещё раз.";
    }

    void TurnOn()
    {
        DefWindowProcW(hwnd, WmSysCommand, ScMonitorPower, -1);
        // На многих ПК «включить» через SC_MONITORPOWER не срабатывает — будим нулевым движением мыши.
        var input = new MouseInput { type = 0, mi = new MouseData { dwFlags = 0x0001 } };
        SendInput(1, [input], Marshal.SizeOf<MouseInput>());
    }

    /// <summary>Слушаем клавиатуру (Raw Input, без перехвата) только пока режим включён.</summary>
    void SetKeepOff(bool keep)
    {
        if (keep == keepOff)
            return;
        keepOff = keep;
        lastKey = 0;
        var device = new RawInputDevice
        {
            usUsagePage = 0x01,
            usUsage = 0x06, // клавиатура
            dwFlags = keep ? 0x00000100u /* RIDEV_INPUTSINK */ : 0x00000001u /* RIDEV_REMOVE */,
            hwndTarget = keep ? hwnd : 0,
        };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (thread is null)
                return;
            if (hwnd != 0)
                PostMessageW(hwnd, WmClose, 0, 0);
            thread.Join(2000);
            thread = null;
            hwnd = 0;
        }
    }

    delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    // INPUT: на x64 MOUSEINPUT начинается со смещения 8 — вложенная структура с nint это и даёт.
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    static extern nint DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    static extern nint SetTimer(nint hwnd, nint id, uint ms, nint func);

    [DllImport("user32.dll")]
    static extern bool KillTimer(nint hwnd, nint id);

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnregisterPowerSettingNotification(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint cbSize, dwTime;
    }

    [DllImport("user32.dll")]
    static extern uint SendInput(uint count, MouseInput[] inputs, int size);

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string? name);
}

/// <summary>
/// Питание самих мониторов по DDC/CI (код VCP 0xD6). Монитор засыпает по команде, хотя Windows
/// считает экран включённым. Не все мониторы это умеют, у некоторых DDC/CI выключен в меню монитора.
/// </summary>
[SupportedOSPlatform("windows")]
static class Ddc
{
    public const uint On = 1, Standby = 4;
    const byte PowerMode = 0xD6;

    /// <summary>true — команду принял хотя бы один монитор.</summary>
    public static bool SetPower(uint mode)
    {
        var any = false;
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
                return true;
            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
                return true;
            foreach (var m in physical)
                any |= SetVCPFeature(m.handle, PowerMode, mode);
            DestroyPhysicalMonitors(count, physical);
            return true;
        }, 0);
        return any;
    }

    delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PhysicalMonitor
    {
        public nint handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string description;
    }

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("dxva2.dll")]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

    [DllImport("dxva2.dll")]
    static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    static extern bool SetVCPFeature(nint monitor, byte code, uint value);

    [DllImport("dxva2.dll")]
    static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
}