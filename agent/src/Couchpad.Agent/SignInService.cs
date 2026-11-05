using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Couchpad.Agent;

/// <summary>
/// Служба Windows «Couchpad: вход в Windows» (от имени системы). Сама она ничего не показывает и сеть не слушает:
/// она держит помощника (<c>--desktop-helper</c>) в сеансе за монитором ПК. Помощник видит экран входа
/// и экран блокировки и может туда нажимать — обычной программе Windows этого не даёт.
/// Помощник в «задании» Windows: служба остановилась или упала — он закрывается вместе с ней.
/// </summary>
static class SignInService
{
    public const string Name = "CouchpadSignIn";

    static nint statusHandle;
    static readonly ManualResetEventSlim stopped = new();
    static readonly AutoResetEvent sessionChanged = new(false);
    static string[] helperArgs = [];

    // Делегаты живут всё время работы службы — их вызывает Windows.
    static readonly ServiceMainProc mainProc = ServiceMain;
    static readonly HandlerProc handlerProc = Handler;

    /// <summary>Точка входа <c>--service</c>. Аргументы помощника — те же --data-dir и --port.</summary>
    public static int Run(string[] args)
    {
        helperArgs = args;
        var table = new[] { new ServiceTableEntry { Name = Name, Proc = mainProc }, default };
        if (!StartServiceCtrlDispatcherW(table))
        {
            Log.Write($"служба: не запущена диспетчером ({Marshal.GetLastWin32Error()})");
            return 1;
        }
        return 0;
    }

    static void ServiceMain(int argc, nint argv)
    {
        statusHandle = RegisterServiceCtrlHandlerExW(Name, handlerProc, 0);
        Report(Running);
        Log.Write("служба запущена");
        try
        {
            Supervise();
        }
        catch (Exception e)
        {
            Log.Write("служба: " + e);
        }
        Log.Write("служба остановлена");
        Report(Stopped);
    }

    static int Handler(int control, int eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Report(StopPending);
                stopped.Set();
                sessionChanged.Set();
                break;
            case ControlSessionChange:
                sessionChanged.Set();
                break;
        }
        return 0;
    }

    /// <summary>Помощник всегда в сеансе за монитором: после выхода из Windows или смены пользователя — перезапуск.</summary>
    static void Supervise()
    {
        Helper? helper = null;
        var failures = 0;
        while (!stopped.IsSet)
        {
            var console = Sessions.Console;
            if (helper is not null && (helper.Exited || helper.Session != console))
            {
                if (helper.Exited)
                {
                    Log.Write($"помощник закрылся (код {helper.ExitCode})");
                    failures++;
                }
                helper.Dispose();
                helper = null;
            }
            if (helper is null && console is { } session)
            {
                try
                {
                    helper = Helper.Start(session, helperArgs);
                    Log.Write($"помощник запущен в сеансе {session}");
                }
                catch (Win32Exception e)
                {
                    Log.Write($"помощник не запустился в сеансе {session}: {e.Message}");
                    failures++;
                }
            }
            // Падает раз за разом — не крутимся впустую: пауза растёт до 30 с.
            var wait = helper is null || failures > 0 ? Math.Min(30_000, 2000 * Math.Max(1, failures)) : 2000;
            sessionChanged.WaitOne(wait);
            if (helper is { Exited: false })
                failures = 0;
        }
        helper?.Dispose();
    }

    static void Report(int state)
    {
        var status = new ServiceStatus
        {
            Type = 0x10, // SERVICE_WIN32_OWN_PROCESS
            State = state,
            ControlsAccepted = state == Running ? AcceptStop | AcceptShutdown | AcceptSessionChange : 0,
            WaitHint = state == Running ? 0 : 10_000,
        };
        SetServiceStatus(statusHandle, ref status);
    }

    /// <summary>Процесс помощника: токен системы, перенесённый в нужный сеанс, рабочий стол winlogon.</summary>
    sealed class Helper : IDisposable
    {
        readonly nint process, job;

        public int Session { get; }

        Helper(int session, nint process, nint job)
        {
            Session = session;
            this.process = process;
            this.job = job;
        }

        public bool Exited => WaitForSingleObject(process, 0) == 0;

        public int ExitCode => GetExitCodeProcess(process, out var code) ? (int)code : -1;

        public static Helper Start(int session, string[] args)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAccess, out var own))
                throw new Win32Exception();
            nint token = 0, job = 0;
            try
            {
                if (!DuplicateTokenEx(own, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out token))
                    throw new Win32Exception();
                var id = session;
                if (!SetTokenInformation(token, TokenSessionId, ref id, sizeof(int)))
                    throw new Win32Exception();

                job = CreateJobObjectW(0, null);
                var limits = new JobExtendedLimits { BasicLimitInformation = new JobBasicLimits { LimitFlags = KillOnJobClose } };
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JobExtendedLimits>());

                var exe = Environment.ProcessPath!;
                var commandLine = $"\"{exe}\" --desktop-helper " + string.Join(' ', args.Select(Quote));
                var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\winlogon" };
                if (!CreateProcessAsUserW(token, exe, commandLine, 0, 0, false, CreateSuspended | CreateNoWindow, 0,
                        Path.GetDirectoryName(exe), ref startup, out var info))
                    throw new Win32Exception();
                AssignProcessToJobObject(job, info.Process);
                ResumeThread(info.Thread);
                CloseHandle(info.Thread);
                var helper = new Helper(session, info.Process, job);
                job = 0;
                return helper;
            }
            finally
            {
                CloseHandle(own);
                if (token != 0) CloseHandle(token);
                if (job != 0) CloseHandle(job);
            }
        }

        static string Quote(string arg) => arg.Contains(' ') || arg.Length == 0 ? $"\"{arg}\"" : arg;

        public void Dispose()
        {
            // Закрытие задания завершает помощника.
            CloseHandle(job);
            WaitForSingleObject(process, 5000);
            CloseHandle(process);
        }
    }

    /// <summary>Журнал службы и помощника: %ProgramData%\Couchpad\signin.log. Без него не понять, что пошло не так у пользователя.</summary>
    public static class Log
    {
        static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Couchpad", "signin.log");
        static readonly Lock gate = new();

        public static void Write(string line)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var file = new FileInfo(path);
                    if (file is { Exists: true, Length: > 1 << 20 })
                        File.Move(path, path + ".old", overwrite: true);
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {line}{Environment.NewLine}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    const int Stopped = 1, StopPending = 3, Running = 4;
    const int AcceptStop = 1, AcceptShutdown = 4, AcceptSessionChange = 0x80;
    const int ControlStop = 1, ControlShutdown = 5, ControlSessionChange = 0x0E;
    const uint TokenAccess = 0x0002 | 0x0008 | 0x0001 | 0x0100 | 0x0080; // DUPLICATE, QUERY, ASSIGN_PRIMARY, ADJUST_SESSIONID, ADJUST_DEFAULT
    const uint MaximumAllowed = 0x02000000;
    const int SecurityImpersonation = 2, TokenPrimary = 1, TokenSessionId = 12;
    const uint CreateSuspended = 0x4, CreateNoWindow = 0x08000000;
    const int JobObjectExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;

    delegate void ServiceMainProc(int argc, nint argv);
    delegate int HandlerProc(int control, int eventType, nint eventData, nint context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ServiceTableEntry
    {
        public string? Name;
        public ServiceMainProc? Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public int Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobExtendedLimits
    {
        public JobBasicLimits BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint RegisterServiceCtrlHandlerExW(string name, HandlerProc handler, nint context);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetTokenInformation(nint token, int infoClass, ref int info, int length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUserW(nint token, string application, string commandLine, nint processAttributes,
        nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(nint job, int infoClass, ref JobExtendedLimits info, int length);

    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool GetExitCodeProcess(nint process, out uint code);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
}
