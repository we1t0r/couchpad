using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>
/// Помощник службы входа (<c>--desktop-helper</c>): от имени системы в сеансе за монитором ПК.
/// <list type="bullet">
/// <item>Пока в Windows никто не вошёл (или компьютер заблокирован) и агента пользователя нет — сам принимает телефоны:
/// экран, нажатия и питание, без файлов и буфера обмена.</item>
/// <item>Пока компьютер заблокирован, а агент пользователя работает — снимает экран блокировки и нажимает туда
/// по его просьбе (<see cref="DesktopBridgeServer"/>).</item>
/// </list>
/// На открытом рабочем столе пользователя помощник ничего не делает — там работает обычный агент.
/// </summary>
static class DesktopHelper
{
    public static int Run(string[] args, string dataDir, int port)
    {
        var log = SignInService.Log.Write;
        var session = Sessions.Current;
        log($"помощник: сеанс {session}, папка {dataDir}");

        var desktop = new DesktopThread();
        // Можно, только пока никто не вошёл или компьютер заблокирован.
        var allowed = Sessions.Cached(() => !Sessions.HasUser(session) || Sessions.IsLocked(session));
        var input = new DesktopInput(desktop, new WindowsInput(), allowed);
        var screen = new DesktopScreen(desktop, new GdiScreen(), allowed);
        var userAgent = SendToPhone.AgentMutex(dataDir);

        using var stop = new CancellationTokenSource();
        var bridge = new DesktopBridgeServer(input, screen, allowed, () => CreatePipe(session),
            pipe => GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var client) && client == session);
        _ = Task.Run(async () =>
        {
            try
            {
                await bridge.RunAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                log("помощник: канал: " + e.Message);
            }
        });

        AgentHost? host = null;
        var retryAt = 0L;
        string? lastState = null;
        log("помощник: жду, когда нужен");
        while (true)
        {
            var agentRunning = MutexExists(userAgent);
            var shouldHost = !agentRunning && allowed();
            // Что видит помощник — в журнал при каждой смене: по нему видно, почему он принимает телефоны или нет.
            var state = $"вошёл пользователь: {Sessions.HasUser(session)}, заблокирован: {Sessions.IsLocked(session)}, "
                        + $"агент пользователя: {agentRunning}, принимать телефоны: {shouldHost}";
            if (state != lastState)
            {
                log("помощник: " + state);
                lastState = state;
            }
            if (shouldHost && host is null && Environment.TickCount64 >= retryAt)
            {
                AgentHost? next = null;
                try
                {
                    log("помощник: запускаю сервер на порту " + port);
                    next = new AgentHost(new AgentOptions(dataDir, port, Input: input, Clipboard: new NullClipboard(), Screen: screen, SignIn: true));
                    next.StartAsync().GetAwaiter().GetResult();
                    host = next;
                    log($"помощник: принимаю телефоны на экране входа (ПК {next.Identity.Id[..8]}, ключ {next.Identity.Fingerprint[..8]})");
                }
                catch (Exception e)
                {
                    next?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                    log("помощник: сервер не запустился: " + e);
                    retryAt = Environment.TickCount64 + 5000;
                }
            }
            else if (!shouldHost && host is not null)
            {
                // Вошли в Windows или запустился агент пользователя — отдаём ему порт.
                host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                host = null;
                log("помощник: экран входа закрыт, телефоны принимает агент пользователя");
            }
            Thread.Sleep(500);
        }
    }

    static bool MutexExists(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Канал открыт вошедшим пользователям, владелец — система: агент так проверяет, что это помощник.</summary>
    static NamedPipeServerStream CreatePipe(int session)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(DesktopBridge.PipeName(session), PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>Сторона агента пользователя: канал помощника его сеанса, если его открыла система.</summary>
    public static DesktopBridgeClient Client() => new(
        () => new NamedPipeClientStream(".", DesktopBridge.PipeName(Sessions.Current), PipeDirection.InOut),
        pipe =>
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        });

    /// <summary>
    /// Поток, который всегда на рабочем столе, куда сейчас идёт ввод: экран входа, блокировки или обычный.
    /// Окон у него нет — иначе Windows не даст ему переходить между рабочими столами.
    /// </summary>
    sealed class DesktopThread
    {
        readonly BlockingCollection<Action> queue = [];
        nint desktop;

        public DesktopThread()
        {
            new Thread(() =>
            {
                foreach (var work in queue.GetConsumingEnumerable())
                {
                    Follow();
                    try
                    {
                        work();
                    }
                    catch (Exception e)
                    {
                        SignInService.Log.Write("помощник: " + e.Message);
                    }
                }
            }) { IsBackground = true, Name = "Рабочий стол" }.Start();
        }

        void Follow()
        {
            var input = OpenInputDesktop(0, false, GenericAll);
            if (input == 0)
                return;
            if (SetThreadDesktop(input))
            {
                if (desktop != 0)
                    CloseDesktop(desktop);
                desktop = input;
            }
            else
            {
                CloseDesktop(input);
            }
        }

        public void Post(Action work) => queue.Add(work);

        public T Invoke<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            });
            return done.Task.GetAwaiter().GetResult();
        }
    }

    sealed class DesktopInput(DesktopThread thread, IInputSink input, Func<bool> allowed) : IInputSink
    {
        void Post(Action<IInputSink> action)
        {
            if (allowed())
                thread.Post(() => action(input));
        }

        public void Move(int dx, int dy) => Post(i => i.Move(dx, dy));
        public void MoveTo(int x, int y) => Post(i => i.MoveTo(x, y));
        public void Button(MouseButton button, bool down) => Post(i => i.Button(button, down));
        public void Wheel(int vertical, int horizontal) => Post(i => i.Wheel(vertical, horizontal));
        public void Key(ushort vk, bool down) => Post(i => i.Key(vk, down));
        public void Text(string text) => Post(i => i.Text(text));
    }

    sealed class DesktopScreen(DesktopThread thread, IScreenSource screen, Func<bool> allowed) : IScreenSource
    {
        public IReadOnlyList<MonitorInfo> Monitors() => thread.Invoke(screen.Monitors);

        public IScreenCapture Open(string? monitorId) => new HelperCapture(thread, thread.Invoke(() => screen.Open(monitorId)), allowed);

        sealed class HelperCapture(DesktopThread thread, IScreenCapture capture, Func<bool> allowed) : IScreenCapture
        {
            public MonitorInfo Monitor => capture.Monitor;

            public ScreenFrame? Capture(int maxWidth, int quality, bool force) => allowed()
                ? thread.Invoke(() => capture.Capture(maxWidth, quality, force))
                : throw new InvalidOperationException("Компьютер разблокирован — экран показывает агент пользователя");

            public void Dispose() => thread.Post(capture.Dispose);
        }
    }

    const uint GenericAll = 0x10000000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll")]
    static extern bool CloseDesktop(nint desktop);

    [DllImport("kernel32.dll")]
    static extern bool GetNamedPipeClientSessionId(nint pipe, out uint session);
}
