using System.Buffers.Binary;
using System.Diagnostics;

namespace RemoteDesk.Agent.Core;

/// <summary>Монитор ПК. Координаты — в физических пикселях рабочего стола Windows.</summary>
public sealed record MonitorInfo(string Id, string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>Кадр экрана в JPEG и его размер после уменьшения.</summary>
public sealed record ScreenFrame(byte[] Jpeg, int Width, int Height);

/// <summary>Откуда берётся картинка экрана. Настоящая (GDI) живёт в приложении агента — ей нужен System.Drawing.</summary>
public interface IScreenSource
{
    IReadOnlyList<MonitorInfo> Monitors();

    /// <summary>Начать снимать монитор (null — основной).</summary>
    IScreenCapture Open(string? monitorId);
}

public interface IScreenCapture : IDisposable
{
    MonitorInfo Monitor { get; }

    /// <summary>
    /// Снять кадр, уменьшить до <paramref name="maxWidth"/> и сжать в JPEG.
    /// null — экран не изменился с прошлого кадра (если не <paramref name="force"/>) или его сейчас не снять (экран блокировки).
    /// </summary>
    ScreenFrame? Capture(int maxWidth, int quality, bool force);
}

/// <summary>Экрана нет (не Windows).</summary>
public sealed class NullScreen : IScreenSource
{
    public IReadOnlyList<MonitorInfo> Monitors() => [];
    public IScreenCapture Open(string? monitorId) => throw new InvalidOperationException("Экран недоступен");
}

/// <summary>
/// Ненастоящий экран для тестов и режима <c>--input-log</c>: синяя картинка 16×9.
/// Автотесты не должны снимать настоящий экран разработчика.
/// </summary>
public sealed class SyntheticScreen : IScreenSource, IVideoSource
{
    static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAJABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDyqiiiv7QPw8//2Q==");

    static readonly MonitorInfo[] All =
    [
        new("test-1", "Монитор 1 (основной)", 0, 0, 1600, 900, true),
        new("test-2", "Монитор 2", 1600, 0, 1600, 900, false),
    ];

    public IReadOnlyList<MonitorInfo> Monitors() => All;

    public IScreenCapture Open(string? monitorId) => new SyntheticCapture(All.FirstOrDefault(m => m.Id == monitorId) ?? All[0]);

    sealed class SyntheticCapture(MonitorInfo monitor) : IScreenCapture
    {
        public MonitorInfo Monitor => monitor;
        public ScreenFrame? Capture(int maxWidth, int quality, bool force) => force ? new ScreenFrame(Jpeg, 16, 9) : null;
        public void Dispose() { }
    }

    public IVideoCapture OpenVideo(MonitorInfo monitor, int maxWidth, int fps, int bitrate) => new SyntheticVideo();

    /// <summary>Ненастоящее видео: ключевой кадр и затем по кадру за вызов — только чтобы проверить протокол.</summary>
    sealed class SyntheticVideo : IVideoCapture
    {
        bool key = true;
        int frames;

        public int Width => 16;
        public int Height => 8;
        public CursorPosition Cursor { get; private set; }
        public CursorShape? Shape { get; } = new(Jpeg, 0, 0, 16, 9);

        public VideoFrame? Next(int timeoutMs)
        {
            Thread.Sleep(Math.Min(timeoutMs, 20));
            if (frames >= 5 && !key)
                return null;
            frames++;
            Cursor = new CursorPosition(0.5, 0.25, true);
            var frame = new VideoFrame(key ? [0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x65, 0x88] : [0, 0, 0, 1, 0x41, 0x9a], key);
            key = false;
            return frame;
        }

        public void RequestKeyFrame() => key = true;

        public void Dispose() { }
    }
}

/// <summary>Телефон просит видео: откуда его брать и с какими настройками.</summary>
internal sealed record VideoRequest(IVideoSource Source, int Fps, int Bitrate);

/// <summary>
/// Поток экрана одному телефону: видео H.264, если его просили и оно доступно, иначе JPEG. Кадр уходит, только
/// если экран изменился; в пути — не больше двух JPEG или четырёх кадров видео (телефон подтверждает каждый),
/// поэтому медленный Wi-Fi не копит задержку, а просто реже обновляет картинку.
/// Видео не вышло (экран блокировки, окно UAC) — показываем JPEG и через пару секунд пробуем видео снова.
/// </summary>
internal sealed class ScreenStream : IDisposable
{
    /// <summary>Тип бинарного кадра WebSocket «картинка экрана».</summary>
    public const byte FrameType = 1;

    /// <summary>Тип бинарного кадра WebSocket «кадр видео H.264».</summary>
    public const byte VideoFrameType = 3;

    const int MaxInFlight = 2;
    const int MaxVideoInFlight = 4;
    const long VideoRetryMs = 2000;

    readonly IScreenCapture capture;
    readonly Func<byte[], Task> send;
    readonly Func<string, Task> sendText;
    readonly VideoRequest? video;
    readonly int maxWidth, quality;
    readonly TimeSpan interval;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim acked = new(0);
    int inFlight;
    long lastAck = Environment.TickCount64;
    volatile bool keyWanted;

    public ScreenStream(IScreenCapture capture, Func<byte[], Task> send, int maxWidth, int quality, int fps,
        VideoRequest? video = null, Func<string, Task>? sendText = null)
    {
        this.capture = capture;
        this.send = send;
        this.sendText = sendText ?? (_ => Task.CompletedTask);
        this.video = video;
        this.maxWidth = Math.Clamp(maxWidth, 320, 3840);
        this.quality = Math.Clamp(quality, 20, 95);
        interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1, 60));
    }

    public MonitorInfo Monitor => capture.Monitor;

    public void Start() => _ = Task.Factory.StartNew(LoopAsync, TaskCreationOptions.LongRunning).Unwrap();

    public void Ack()
    {
        if (Interlocked.Decrement(ref inFlight) < 0)
            Interlocked.Exchange(ref inFlight, 0);
        Volatile.Write(ref lastAck, Environment.TickCount64);
        acked.Release();
    }

    /// <summary>Телефон заново открыл декодер — следующему кадру видео быть ключевым.</summary>
    public void RequestKeyFrame() => keyWanted = true;

    async Task LoopAsync()
    {
        var ct = stop.Token;
        long number = 0;
        IVideoCapture? encoder = null;
        var retryVideoAt = 0L;
        var videoFailures = 0;
        var forceJpeg = true;
        var cursor = new CursorSender(sendText);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref inFlight) >= (encoder is null ? MaxInFlight : MaxVideoInFlight))
                {
                    await acked.WaitAsync(500, ct);
                    // Подтверждение потерялось — не стоим вечно.
                    if (Environment.TickCount64 - Volatile.Read(ref lastAck) > 3000)
                        Interlocked.Exchange(ref inFlight, 0);
                    continue;
                }

                if (video is not null && encoder is null && Environment.TickCount64 >= retryVideoAt)
                {
                    try
                    {
                        encoder = video.Source.OpenVideo(capture.Monitor, maxWidth, video.Fps, video.Bitrate);
                        keyWanted = false;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Не вышло снова и снова — пробуем всё реже (до 30 с): открытие кодировщика небесплатно.
                        if (e is not VideoUnavailableException || videoFailures == 0)
                            AgentLog.Write("видео не открылось, пока JPEG: " + e.Message);
                        // Блокировка проверяется мгновенно — её ждём без замедления.
                        retryVideoAt = Environment.TickCount64 + (e is VideoUnavailableException ? VideoRetryMs : Math.Min(VideoRetryMs << Math.Min(videoFailures, 4), 30_000));
                        videoFailures++;
                    }
                }

                if (encoder is not null)
                {
                    VideoFrame? frame;
                    try
                    {
                        if (keyWanted)
                        {
                            keyWanted = false;
                            encoder.RequestKeyFrame();
                        }
                        frame = encoder.Next(50);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Экран блокировки, окно UAC, смена разрешения — пока JPEG.
                        if (e is not VideoUnavailableException)
                            AgentLog.Write("видео прервалось, пока JPEG: " + e.Message);
                        encoder.Dispose();
                        encoder = null;
                        retryVideoAt = Environment.TickCount64 + VideoRetryMs;
                        forceJpeg = true;
                        await cursor.HideAsync();
                        continue;
                    }
                    await cursor.SendAsync(encoder);
                    if (frame is not null)
                    {
                        videoFailures = 0;
                        number++;
                        Interlocked.Increment(ref inFlight);
                        await send(PackVideo(number, encoder.Width, encoder.Height, frame));
                    }
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                ScreenFrame? image = null;
                try
                {
                    image = capture.Capture(maxWidth, quality, force: forceJpeg);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Экран блокировки или окно UAC: снять нельзя, ждём.
                }

                if (image is not null)
                {
                    forceJpeg = false;
                    number++;
                    Interlocked.Increment(ref inFlight);
                    await send(Pack(number, image));
                }
                var wait = (image is null ? TimeSpan.FromMilliseconds(40) : interval) - Stopwatch.GetElapsedTime(started);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            encoder?.Dispose();
            capture.Dispose();
        }
    }

    /// <summary>[1 байт тип][4 байта номер][2 байта ширина][2 байта высота][JPEG] — числа big-endian.</summary>
    internal static byte[] Pack(long number, ScreenFrame frame)
    {
        var data = new byte[9 + frame.Jpeg.Length];
        data[0] = FrameType;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1), (uint)number);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5), (ushort)frame.Width);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(7), (ushort)frame.Height);
        frame.Jpeg.CopyTo(data, 9);
        return data;
    }

    /// <summary>[3][4 байта номер][2 байта ширина][2 байта высота][1 байт: 1 — ключевой][H.264 Annex B] — big-endian.</summary>
    internal static byte[] PackVideo(long number, int width, int height, VideoFrame frame)
    {
        var data = new byte[10 + frame.Data.Length];
        data[0] = VideoFrameType;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1), (uint)number);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(7), (ushort)height);
        data[9] = frame.Key ? (byte)1 : (byte)0;
        frame.Data.CopyTo(data, 10);
        return data;
    }

    public void Dispose() => stop.Cancel();

    /// <summary>В видео курсора нет: шлём телефону его место (не чаще 60 раз в секунду) и картинку, когда сменилась.</summary>
    sealed class CursorSender(Func<string, Task> sendText)
    {
        CursorPosition last = new(-1, -1, false);
        CursorShape? shape;
        long sentAt;

        public async Task SendAsync(IVideoCapture video)
        {
            if (video.Shape is { } s && !ReferenceEquals(s, shape))
            {
                shape = s;
                await sendText(Message.Build("cursor_shape", ("png", Convert.ToBase64String(s.Png)),
                    ("hx", s.HotX), ("hy", s.HotY), ("w", s.Width), ("h", s.Height)));
            }
            var c = video.Cursor;
            if (c == last || (c.Visible == last.Visible && Environment.TickCount64 - sentAt < 15))
                return;
            last = c;
            sentAt = Environment.TickCount64;
            await sendText(Message.Build("cursor", ("x", Math.Round(c.X, 4)), ("y", Math.Round(c.Y, 4)), ("v", c.Visible)));
        }

        /// <summary>JPEG рисует курсор сам — телефонный прячем.</summary>
        public async Task HideAsync()
        {
            if (!last.Visible)
                return;
            last = last with { Visible = false };
            await sendText(Message.Build("cursor", ("x", last.X), ("y", last.Y), ("v", false)));
        }
    }
}
