using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Couchpad.Agent;

/// <summary>
/// Запуск агента пользователя из помощника службы входа — после того как Windows закрыла агента при перезагрузке
/// и отменила её (см. <see cref="Core.Revive"/>). Агент запускается от имени вошедшего пользователя, без прав
/// администратора, на его рабочем столе — так же, как его запустил бы автозапуск.
/// </summary>
static class AgentRevive
{
    /// <summary>Аргумент агента: его запустил помощник после отменённой перезагрузки.</summary>
    public const string Arg = "--revived";

    public static void Start(int session, string dataDir, int port)
    {
        if (!WTSQueryUserToken((uint)session, out var token))
            throw new Win32Exception();
        nint environment = 0;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
                throw new Win32Exception();
            var exe = Environment.ProcessPath!;
            var commandLine = $"\"{exe}\" {Arg} --data-dir \"{dataDir}\" --port {port}";
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            // Помощник живёт в задании, которое закрывается вместе с ним, — агент должен из него выйти.
            if (!CreateProcessAsUserW(token, exe, commandLine, 0, 0, false, UnicodeEnvironment | BreakawayFromJob, environment,
                    Path.GetDirectoryName(exe), ref startup, out var info))
                throw new Win32Exception();
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
        finally
        {
            if (environment != 0) DestroyEnvironmentBlock(environment);
            CloseHandle(token);
        }
    }

    const uint UnicodeEnvironment = 0x00000400, BreakawayFromJob = 0x01000000;

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

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint session, out nint token);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);

    [DllImport("userenv.dll")]
    static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUserW(nint token, string application, string commandLine, nint processAttributes,
        nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
}
