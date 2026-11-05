using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Couchpad.Agent.Core;

namespace Couchpad.Agent;

/// <summary>
/// Звук из колонок ПК через WASAPI loopback: Windows отдаёт то, что идёт на устройство вывода по умолчанию,
/// без драйверов и разрешений. Приводим к 48 кГц стерео float.
/// <para>
/// «Заглушить колонки» — громкость устройства в ноль. Не Mute: на части звуковых карт выключенный звук
/// приходит и в запись тишиной, а громкость на запись не влияет. Прежняя громкость записана в файл — если
/// агент упадёт, она вернётся при следующем запуске (<see cref="RestoreAfterCrash"/>).
/// </para>
/// </summary>
sealed class WasapiAudio(string dataDir) : IAudioSource
{
    readonly object gate = new();
    int muters;
    MMDevice? mutedDevice;
    float savedVolume;

    string SavedFile => Path.Combine(dataDir, "speakers-volume.txt");

    public IDisposable Start(bool muteSpeakers, Action<float[], int> samples)
    {
        // Устаревший, но простой и проверенный способ записать всё, что играет; новый WasapiRecorder пока не нужен.
#pragma warning disable CS0618
        var capture = new WasapiLoopbackCapture();
#pragma warning restore CS0618
        var format = capture.WaveFormat;
        var converter = new Converter(format);
        capture.DataAvailable += (_, e) =>
        {
            var (buffer, count) = converter.Convert(e.Buffer, e.BytesRecorded);
            if (count > 0)
                samples(buffer, count);
        };
        capture.StartRecording();
        if (muteSpeakers)
            Mute();
        return new Stop(() =>
        {
            if (muteSpeakers)
                Unmute();
            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
            }
            capture.Dispose();
        });
    }

    void Mute()
    {
        lock (gate)
        {
            if (muters++ > 0)
                return;
            try
            {
                using var devices = new MMDeviceEnumerator();
                mutedDevice = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                savedVolume = mutedDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                File.WriteAllText(SavedFile, mutedDevice.ID + "\n" + savedVolume.ToString(CultureInfo.InvariantCulture));
                mutedDevice.AudioEndpointVolume.MasterVolumeLevelScalar = 0;
            }
            catch (Exception)
            {
                mutedDevice = null;
            }
        }
    }

    void Unmute()
    {
        lock (gate)
        {
            if (--muters > 0 || mutedDevice is null)
                return;
            try
            {
                // Пока звук шёл на телефон, громкость подняли на самом ПК — так и оставляем.
                if (mutedDevice.AudioEndpointVolume.MasterVolumeLevelScalar < 0.005f)
                    mutedDevice.AudioEndpointVolume.MasterVolumeLevelScalar = savedVolume;
                File.Delete(SavedFile);
            }
            catch (Exception)
            {
            }
            mutedDevice.Dispose();
            mutedDevice = null;
        }
    }

    /// <summary>Агент упал, пока колонки были заглушены, — вернуть им громкость.</summary>
    public void RestoreAfterCrash()
    {
        try
        {
            if (!File.Exists(SavedFile))
                return;
            var lines = File.ReadAllLines(SavedFile);
            File.Delete(SavedFile);
            if (lines.Length < 2 || !float.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var volume))
                return;
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDevice(lines[0]);
            if (device.AudioEndpointVolume.MasterVolumeLevelScalar < 0.005f)
                device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
        }
        catch (Exception)
        {
        }
    }

    sealed class Stop(Action action) : IDisposable
    {
        int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
                action();
        }
    }

    /// <summary>Из формата устройства (обычно float, 44,1 или 48 кГц, 2–8 каналов) — в 48 кГц стерео float.</summary>
    sealed class Converter(WaveFormat format)
    {
        readonly int channels = format.Channels;
        readonly double step = (double)format.SampleRate / IAudioSource.Rate;
        // Общий формат WASAPI с 32 битами — на деле всегда float, даже если записан как «extensible».
        readonly bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
        readonly int bytes = format.BitsPerSample / 8;
        float[] output = new float[4096];
        double position; // дробная позиция в исходных кадрах для передискретизации

        public (float[] Buffer, int Count) Convert(byte[] data, int length)
        {
            var frames = length / (bytes * channels);
            var outFrames = (int)((frames - position) / step) + 2;
            if (output.Length < outFrames * 2)
                output = new float[outFrames * 2];
            var count = 0;
            while (position < frames)
            {
                var i = (int)position;
                var frac = (float)(position - i);
                var (l0, r0) = Frame(data, i);
                var (l1, r1) = i + 1 < frames ? Frame(data, i + 1) : (l0, r0);
                output[count++] = l0 + (l1 - l0) * frac;
                output[count++] = r0 + (r1 - r0) * frac;
                position += step;
            }
            position -= frames;
            return (output, count);
        }

        (float L, float R) Frame(byte[] data, int frame)
        {
            var offset = frame * bytes * channels;
            var l = Sample(data, offset);
            // Моно — в оба уха; 5.1 и больше — берём передние левый и правый.
            var r = channels > 1 ? Sample(data, offset + bytes) : l;
            return (l, r);
        }

        float Sample(byte[] data, int offset) => (isFloat, bytes) switch
        {
            (true, 4) => BitConverter.ToSingle(data, offset),
            (false, 2) => BitConverter.ToInt16(data, offset) / 32768f,
            (false, 3) => ((data[offset] << 8 | data[offset + 1] << 16 | data[offset + 2] << 24) >> 8) / 8388608f,
            (false, 4) => BitConverter.ToInt32(data, offset) / 2147483648f,
            _ => 0,
        };
    }
}
