using System.Buffers.Binary;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;

namespace Couchpad.Agent.Core;

/// <summary>Звук ПК — то, что сейчас играет в колонках.</summary>
public interface IAudioSource
{
    /// <summary>Частота кадров на входе <see cref="AudioStream"/>: 48 кГц, стерео, float, L R чередуются.</summary>
    const int Rate = 48000;

    /// <summary>
    /// Начать запись: <paramref name="samples"/> получает куски звука (буфер и число значений в нём).
    /// <paramref name="muteSpeakers"/> — заглушить колонки ПК, пока идёт запись; Dispose возвращает как было.
    /// </summary>
    IDisposable Start(bool muteSpeakers, Action<float[], int> samples);
}

public sealed class NullAudio : IAudioSource
{
    public IDisposable Start(bool muteSpeakers, Action<float[], int> samples) =>
        throw new InvalidOperationException("Этот компьютер не умеет передавать звук");
}

/// <summary>Ненастоящий звук для автотестов: тон 440 Гц. Настоящий звук ПК тесты не записывают.</summary>
public sealed class SyntheticAudio : IAudioSource
{
    public IDisposable Start(bool muteSpeakers, Action<float[], int> samples)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            const int chunk = IAudioSource.Rate / 100; // 10 мс
            var buffer = new float[chunk * 2];
            long t = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    for (var i = 0; i < chunk; i++, t++)
                        buffer[2 * i] = buffer[2 * i + 1] = 0.2f * MathF.Sin(2 * MathF.PI * 440 * t / IAudioSource.Rate);
                    samples(buffer, buffer.Length);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        return stop;
    }
}

/// <summary>
/// Звук одному телефону: Opus по 20 мс. Отправляет отдельный цикл через короткую очередь: если сеть
/// не успевает, старые куски выбрасываются — звук прерывается, но не отстаёт от картинки всё больше.
/// </summary>
internal sealed class AudioStream : IDisposable
{
    /// <summary>Тип бинарного кадра WebSocket «звук».</summary>
    public const byte FrameType = 2;
    public const int FrameSamples = IAudioSource.Rate / 50; // 20 мс

    readonly IOpusEncoder encoder;
    readonly float[] pending = new float[FrameSamples * 2];
    readonly byte[] packet = new byte[1500];
    readonly Channel<byte[]> queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly CancellationTokenSource stop = new();
    readonly Func<byte[], Task> send;
    readonly IDisposable capture;
    int filled;
    long number;

    public AudioStream(IAudioSource source, Func<byte[], Task> send, bool muteSpeakers, int bitrate)
    {
        this.send = send;
        encoder = OpusCodecFactory.CreateEncoder(IAudioSource.Rate, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = Math.Clamp(bitrate, 16_000, 256_000);
        _ = Task.Run(SendLoopAsync);
        capture = source.Start(muteSpeakers, OnSamples);
    }

    void OnSamples(float[] buffer, int count)
    {
        lock (pending)
        {
            for (var i = 0; i < count;)
            {
                var take = Math.Min(count - i, pending.Length - filled);
                Array.Copy(buffer, i, pending, filled, take);
                filled += take;
                i += take;
                if (filled < pending.Length)
                    break;
                filled = 0;
                var length = encoder.Encode(pending, FrameSamples, packet, packet.Length);
                if (length > 0)
                    queue.Writer.TryWrite(Pack(++number, packet.AsSpan(0, length)));
            }
        }
    }

    async Task SendLoopAsync()
    {
        try
        {
            await foreach (var data in queue.Reader.ReadAllAsync(stop.Token))
                await send(data);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>[1 байт тип][4 байта номер][Opus, 20 мс, 48 кГц стерео] — номер big-endian.</summary>
    internal static byte[] Pack(long number, ReadOnlySpan<byte> opus)
    {
        var data = new byte[5 + opus.Length];
        data[0] = FrameType;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1), (uint)number);
        opus.CopyTo(data.AsSpan(5));
        return data;
    }

    public void Dispose()
    {
        capture.Dispose();
        stop.Cancel();
    }
}
