using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteDesk.Agent.Core;

/// <summary>
/// Канал между агентом пользователя и помощником службы «Вход в Windows». Пока компьютер заблокирован,
/// экран блокировки живёт на защищённом рабочем столе: обычная программа его не видит и не может туда нажимать.
/// Помощник (работает от имени системы) снимает этот экран и передаёт нажатия по просьбе агента.
///
/// Кадр канала: [u32 длина, little-endian][данные]. Запрос — JSON {"op": ...}.
/// Ответ есть только у monitors (JSON) и capture ([u8 состояние][u16 ширина][u16 высота][JPEG]).
/// </summary>
public static class DesktopBridge
{
    public const int Version = 1;

    /// <summary>Канал помощника в сеансе Windows (у каждого вошедшего пользователя свой сеанс).</summary>
    public static string PipeName(int sessionId) => $"Couchpad.Desktop.{sessionId}";

    internal const byte NoChange = 0, Frame = 1, Unavailable = 2;
    const int MaxFrame = 64 << 20;

    internal static void Write(Stream stream, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
        stream.Flush();
    }

    internal static void Write(Stream stream, JsonObject msg) => Write(stream, Encoding.UTF8.GetBytes(msg.ToJsonString()));

    /// <summary>null — канал закрыт.</summary>
    internal static byte[]? Read(Stream stream)
    {
        var length = new byte[4];
        if (!ReadExactly(stream, length))
            return null;
        var n = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (n is < 0 or > MaxFrame)
            throw new IOException("Слишком большой кадр канала");
        var data = new byte[n];
        return ReadExactly(stream, data) ? data : null;
    }

    static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// Сторона помощника: принимает агента пользователя и выполняет его просьбы на текущем рабочем столе.
/// <paramref name="allowed"/> — можно ли сейчас (компьютер заблокирован или никто не вошёл); иначе
/// кадров нет, а нажатия отбрасываются: открытый рабочий стол пользователя через помощника не управляется.
/// </summary>
public sealed class DesktopBridgeServer(IInputSink input, IScreenSource screen, Func<bool> allowed,
    Func<NamedPipeServerStream> createPipe, Func<NamedPipeServerStream, bool>? acceptClient = null)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = createPipe();
            }
            catch (IOException)
            {
                // Имя занято (старый помощник ещё закрывается) — попробуем чуть позже.
                await Task.Delay(1000, ct);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
            if (acceptClient is not null && !acceptClient(pipe))
            {
                await pipe.DisposeAsync();
                continue;
            }
            _ = Task.Run(() => Serve(pipe), CancellationToken.None);
        }
    }

    void Serve(NamedPipeServerStream pipe)
    {
        var captures = new Dictionary<string, IScreenCapture>();
        try
        {
            while (DesktopBridge.Read(pipe) is { } data)
            {
                if (JsonNode.Parse(data) is not JsonObject msg)
                    continue;
                Handle(pipe, msg, captures);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or ObjectDisposedException)
        {
        }
        finally
        {
            foreach (var capture in captures.Values)
                capture.Dispose();
            pipe.Dispose();
        }
    }

    void Handle(Stream pipe, JsonObject msg, Dictionary<string, IScreenCapture> captures)
    {
        switch (msg.Str("op"))
        {
            case "monitors":
                DesktopBridge.Write(pipe, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(screen.Monitors(), Message.Options)));
                return;
            case "capture":
                DesktopBridge.Write(pipe, CaptureReply(msg, captures));
                return;
            case "secure":
                // На экране защищённый стол (блокировка, вход, окно UAC) — агенту пора идти через помощника.
                DesktopBridge.Write(pipe, new[] { allowed() ? (byte)1 : (byte)0 });
                return;
        }

        if (!allowed())
            return;
        switch (msg.Str("op"))
        {
            case "move":
                input.Move((int)(msg.Num("x") ?? 0), (int)(msg.Num("y") ?? 0));
                break;
            case "moveto":
                input.MoveTo((int)(msg.Num("x") ?? 0), (int)(msg.Num("y") ?? 0));
                break;
            case "button" when Enum.TryParse<MouseButton>(msg.Str("b"), out var button):
                input.Button(button, msg.Bool("down") ?? false);
                break;
            case "wheel":
                input.Wheel((int)(msg.Num("v") ?? 0), (int)(msg.Num("h") ?? 0));
                break;
            case "key":
                input.Key((ushort)(msg.Num("vk") ?? 0), msg.Bool("down") ?? false);
                break;
            case "text":
                input.Text(msg.Str("text") ?? "");
                break;
        }
    }

    byte[] CaptureReply(JsonObject msg, Dictionary<string, IScreenCapture> captures)
    {
        if (!allowed())
            return [DesktopBridge.Unavailable];
        try
        {
            var id = msg.Str("monitor") ?? "";
            if (!captures.TryGetValue(id, out var capture))
                captures[id] = capture = screen.Open(id.Length == 0 ? null : id);
            var frame = capture.Capture((int)(msg.Num("maxWidth") ?? 1600), (int)(msg.Num("quality") ?? 70), msg.Bool("force") ?? false);
            if (frame is null)
                return [DesktopBridge.NoChange];
            var reply = new byte[5 + frame.Jpeg.Length];
            reply[0] = DesktopBridge.Frame;
            BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(1), (ushort)frame.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(3), (ushort)frame.Height);
            frame.Jpeg.CopyTo(reply, 5);
            return reply;
        }
        catch (Exception e) when (e is not IOException)
        {
            return [DesktopBridge.Unavailable];
        }
    }
}

/// <summary>
/// Сторона агента пользователя: ввод и экран через помощника. Если помощника нет (служба не установлена),
/// нажатия пропадают, а кадров нет — как и без него.
/// <paramref name="trustServer"/> проверяет, что канал открыл помощник службы, а не чужая программа:
/// иначе она могла бы подслушать PIN, набранный на телефоне.
/// </summary>
public sealed class DesktopBridgeClient(Func<NamedPipeClientStream> createPipe, Func<NamedPipeClientStream, bool>? trustServer = null)
    : IInputSink, IScreenSource, IDisposable
{
    readonly Lock gate = new();
    NamedPipeClientStream? pipe;
    long retryAt;

    /// <summary>Помощник отвечает (или отвечал до последнего обрыва).</summary>
    public bool Available
    {
        get { lock (gate) return Connected() is not null; }
    }

    NamedPipeClientStream? Connected()
    {
        if (pipe is { IsConnected: true })
            return pipe;
        pipe?.Dispose();
        pipe = null;
        // Нет помощника — не стучимся на каждое нажатие.
        if (Environment.TickCount64 < retryAt)
            return null;
        var next = createPipe();
        try
        {
            next.Connect(300);
            if (trustServer is null || trustServer(next))
                return pipe = next;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
        }
        next.Dispose();
        retryAt = Environment.TickCount64 + 2000;
        return null;
    }

    byte[]? Call(JsonObject msg, bool reply)
    {
        lock (gate)
        {
            if (Connected() is not { } p)
                return null;
            try
            {
                DesktopBridge.Write(p, msg);
                return reply ? DesktopBridge.Read(p) : [];
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                pipe?.Dispose();
                pipe = null;
                return null;
            }
        }
    }

    void Send(string op, params (string Key, JsonNode? Value)[] fields)
    {
        var msg = new JsonObject { ["op"] = op };
        foreach (var (key, value) in fields)
            msg[key] = value;
        Call(msg, reply: false);
    }

    /// <summary>
    /// Помощник видит на экране защищённый стол: блокировку, вход или окно UAC. Сам агент пользователя этого
    /// надёжно не узнает — Windows не пускает его туда даже посмотреть. Нет помощника — false.
    /// </summary>
    public bool SecureDesktop() => Call(new JsonObject { ["op"] = "secure" }, reply: true) is [1];

    public void Move(int dx, int dy) => Send("move", ("x", dx), ("y", dy));
    public void MoveTo(int x, int y) => Send("moveto", ("x", x), ("y", y));
    public void Button(MouseButton button, bool down) => Send("button", ("b", button.ToString()), ("down", down));
    public void Wheel(int vertical, int horizontal) => Send("wheel", ("v", vertical), ("h", horizontal));
    public void Key(ushort vk, bool down) => Send("key", ("vk", vk), ("down", down));
    public void Text(string text) => Send("text", ("text", text));

    public IReadOnlyList<MonitorInfo> Monitors()
    {
        var data = Call(new JsonObject { ["op"] = "monitors" }, reply: true);
        if (data is null)
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<MonitorInfo>>(data, Message.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public IScreenCapture Open(string? monitorId)
    {
        var monitors = Monitors();
        var monitor = monitors.FirstOrDefault(m => m.Id == monitorId) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault()
            ?? throw new InvalidOperationException("Экран блокировки сейчас не снять");
        return new BridgeCapture(this, monitor);
    }

    internal ScreenFrame? Capture(string monitorId, int maxWidth, int quality, bool force)
    {
        var data = Call(new JsonObject
        {
            ["op"] = "capture", ["monitor"] = monitorId, ["maxWidth"] = maxWidth, ["quality"] = quality, ["force"] = force,
        }, reply: true);
        if (data is null || data.Length == 0 || data[0] == DesktopBridge.Unavailable)
            throw new InvalidOperationException("Экран блокировки сейчас не снять");
        if (data[0] != DesktopBridge.Frame || data.Length < 5)
            return null;
        return new ScreenFrame(data[5..], BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(3)));
    }

    sealed class BridgeCapture(DesktopBridgeClient client, MonitorInfo monitor) : IScreenCapture
    {
        public MonitorInfo Monitor => monitor;
        public ScreenFrame? Capture(int maxWidth, int quality, bool force) => client.Capture(monitor.Id, maxWidth, quality, force);
        public void Dispose() { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            pipe?.Dispose();
            pipe = null;
        }
    }
}

/// <summary>
/// Ввод и экран, которые переключаются на <paramref name="locked"/>-источник, пока компьютер заблокирован.
/// Так телефон, подключённый к агенту пользователя, видит экран блокировки и может набрать PIN.
/// </summary>
public sealed class LockAwareDesktop(IInputSink input, IScreenSource screen, IInputSink lockedInput, IScreenSource lockedScreen, Func<bool> isLocked)
    : IInputSink, IScreenSource, IVideoSource
{
    readonly Func<bool> locked = isLocked;
    readonly IScreenSource lockScreen = lockedScreen;

    IInputSink Input => locked() ? lockedInput : input;

    public void Move(int dx, int dy) => Input.Move(dx, dy);
    public void MoveTo(int x, int y) => Input.MoveTo(x, y);
    public void Button(MouseButton button, bool down) => Input.Button(button, down);
    public void Wheel(int vertical, int horizontal) => Input.Wheel(vertical, horizontal);
    public void Key(ushort vk, bool down) => Input.Key(vk, down);
    public void Text(string text) => Input.Text(text);

    // Список мониторов — всегда свой: он не меняется от блокировки, а помощника может и не быть.
    public IReadOnlyList<MonitorInfo> Monitors() => screen.Monitors();

    public IScreenCapture Open(string? monitorId) => new LockAwareCapture(this, screen.Open(monitorId));

    /// <summary>Видео — только со своего рабочего стола: экран блокировки идёт картинками через помощника.</summary>
    public IVideoCapture OpenVideo(MonitorInfo monitor, int maxWidth, int fps, int bitrate)
    {
        if (screen is not IVideoSource video)
            throw new VideoUnavailableException("Видео не поддерживается");
        if (locked())
            throw new VideoUnavailableException("Компьютер заблокирован");
        return new LockAwareVideo(locked, video.OpenVideo(monitor, maxWidth, fps, bitrate));
    }

    sealed class LockAwareVideo(Func<bool> locked, IVideoCapture video) : IVideoCapture
    {
        public int Width => video.Width;
        public int Height => video.Height;
        public CursorPosition Cursor => video.Cursor;
        public CursorShape? Shape => video.Shape;

        public VideoFrame? Next(int timeoutMs) =>
            locked() ? throw new VideoUnavailableException("Компьютер заблокирован") : video.Next(timeoutMs);

        public void RequestKeyFrame() => video.RequestKeyFrame();

        public void Dispose() => video.Dispose();
    }

    /// <summary>Снимает свой экран или экран блокировки — смотря что сейчас на мониторе.</summary>
    sealed class LockAwareCapture(LockAwareDesktop owner, IScreenCapture normal) : IScreenCapture
    {
        IScreenCapture? locked;
        bool wasLocked;

        public MonitorInfo Monitor => normal.Monitor;

        public ScreenFrame? Capture(int maxWidth, int quality, bool force)
        {
            var isLocked = owner.locked();
            // После смены экрана кадр нужен сразу, даже если «ничего не изменилось» с прошлого раза.
            force |= isLocked != wasLocked;
            wasLocked = isLocked;
            if (!isLocked)
                return normal.Capture(maxWidth, quality, force);
            locked ??= owner.lockScreen.Open(normal.Monitor.Id);
            return locked.Capture(maxWidth, quality, force);
        }

        public void Dispose()
        {
            normal.Dispose();
            locked?.Dispose();
        }
    }
}
