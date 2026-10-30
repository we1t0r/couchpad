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
public sealed class SyntheticScreen : IScreenSource
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
}

/// <summary>
/// Поток кадров одному телефону. Кадр уходит, только если экран изменился; в пути — не больше двух кадров
/// (телефон подтверждает каждый), поэтому медленный Wi-Fi не копит задержку, а просто реже обновляет картинку.
/// </summary>
internal sealed class ScreenStream : IDisposable
{
    /// <summary>Тип бинарного кадра WebSocket «картинка экрана».</summary>
    public const byte FrameType = 1;
    const int MaxInFlight = 2;

    readonly IScreenCapture capture;
    readonly Func<byte[], Task> send;
    readonly int maxWidth, quality;
    readonly TimeSpan interval;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim acked = new(0);
    int inFlight;
    long lastAck = Environment.TickCount64;

    public ScreenStream(IScreenCapture capture, Func<byte[], Task> send, int maxWidth, int quality, int fps)
    {
        this.capture = capture;
        this.send = send;
        this.maxWidth = Math.Clamp(maxWidth, 320, 3840);
        this.quality = Math.Clamp(quality, 20, 95);
        interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 1, 60));
    }

    public MonitorInfo Monitor => capture.Monitor;

    public void Start() => _ = Task.Run(LoopAsync);

    public void Ack()
    {
        if (Interlocked.Decrement(ref inFlight) < 0)
            Interlocked.Exchange(ref inFlight, 0);
        Volatile.Write(ref lastAck, Environment.TickCount64);
        acked.Release();
    }

    async Task LoopAsync()
    {
        var ct = stop.Token;
        long number = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref inFlight) >= MaxInFlight)
                {
                    await acked.WaitAsync(500, ct);
                    // Подтверждение потерялось — не стоим вечно.
                    if (Environment.TickCount64 - Volatile.Read(ref lastAck) > 3000)
                        Interlocked.Exchange(ref inFlight, 0);
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                ScreenFrame? frame = null;
                try
                {
                    frame = capture.Capture(maxWidth, quality, force: number == 0);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Экран блокировки или окно UAC: снять нельзя, ждём.
                }

                if (frame is not null)
                {
                    number++;
                    Interlocked.Increment(ref inFlight);
                    await send(Pack(number, frame));
                }
                var wait = (frame is null ? TimeSpan.FromMilliseconds(40) : interval) - Stopwatch.GetElapsedTime(started);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
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

    public void Dispose() => stop.Cancel();
}
