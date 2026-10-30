using System.Diagnostics;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk.Agent;

/// <summary>
/// <c>--video-selftest файл</c>: сжать 90 ненастоящих кадров (бегущие полосы) кодировщиком H.264 и записать
/// видео в файл, а отчёт — рядом в .txt. Экран не снимается. Для проверки кодировщика на новом ПК.
/// </summary>
static class VideoSelfTest
{
    public static int Run(string path)
    {
        var log = new StringBuilder();
        try
        {
            const int width = 1280, height = 720, fps = 60;
            var watch = Stopwatch.StartNew();
            using var encoder = new H264Encoder(width, height, fps, 6_000_000);
            log.AppendLine($"кодировщик: {encoder.Name}, открыт за {watch.ElapsedMilliseconds} мс");
            var frame = new byte[width * height * 3 / 2];
            using var output = File.Create(path);
            var total = 0;
            for (var n = 0; n < 90; n++)
            {
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        frame[y * width + x] = (byte)(((x + n * 8) / 40 % 2 == 0 ? 200 : 40) + y / 24);
                frame.AsSpan(width * height).Fill(128);
                if (n == 45)
                    encoder.RequestKeyFrame();
                watch.Restart();
                foreach (var f in encoder.Encode(frame))
                {
                    output.Write(f.Data);
                    total++;
                    log.AppendLine($"кадр {n}: {f.Data.Length} байт{(f.Key ? ", ключевой" : "")}, NAL {string.Join(",", Nals(f.Data))}, {watch.Elapsed.TotalMilliseconds:F1} мс");
                }
            }
            log.AppendLine($"всего кадров на выходе: {total}");
            return total > 0 && ConverterWorks(log) ? 0 : 1;
        }
        catch (Exception e)
        {
            log.AppendLine("ошибка: " + e);
            return 1;
        }
        finally
        {
            File.WriteAllText(Path.ChangeExtension(path, ".txt"), log.ToString());
        }
    }

    /// <summary>Видеопроцессор: белая и чёрная половины 1920×1080 → NV12 1280×720, яркость 235 и 16, цвет 128.</summary>
    static bool ConverterWorks(StringBuilder log)
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var device, out var context).CheckError();
        using (device)
        using (context)
        {
            const int w = 1920, h = 1080;
            var pixels = new uint[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    pixels[y * w + x] = x < w / 2 ? 0xFFFFFFFF : 0xFF000000;
            using var texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            context.UpdateSubresource<uint>(pixels, texture, 0, w * 4);
            using var converter = new Nv12Converter(device, context, w, h, 1280, 60);
            var watch = Stopwatch.StartNew();
            converter.CopyFrom(texture);
            var nv12 = converter.Convert();
            var white = nv12[360 * converter.Width + 100];
            var black = nv12[360 * converter.Width + 1180];
            var chroma = nv12[converter.Width * converter.Height + 180 * converter.Width + 100];
            log.AppendLine($"видеопроцессор: {converter.Width}×{converter.Height} за {watch.Elapsed.TotalMilliseconds:F1} мс; белый Y={white}, чёрный Y={black}, цвет {chroma}");
            return white is >= 225 and <= 240 && black is >= 12 and <= 24 && chroma is >= 124 and <= 132;
        }
    }

    static IEnumerable<int> Nals(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                yield return data[i + 3] & 0x1f;
    }
}
