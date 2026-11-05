using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Couchpad.Agent.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Couchpad.Agent;

/// <summary>
/// Видео монитора через видеокарту: DXGI Desktop Duplication отдаёт кадр, только когда экран изменился,
/// видеопроцессор той же видеокарты уменьшает его и переводит в NV12, кодировщик сжимает в H.264.
/// Курсора в кадре нет — его место и картинку забирает телефон отдельно.
/// </summary>
sealed class DxgiVideo : IVideoCapture
{
    static readonly int WaitTimeout = unchecked((int)0x887A0027);

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly IDXGIOutputDuplication duplication;
    readonly Nv12Converter converter;
    readonly H264Encoder encoder;
    readonly int sourceWidth, sourceHeight;
    readonly long minFrameTicks;
    long lastFrameAt;
    bool keyWanted = true;
    bool pending, hasFrame;

    // Экран замер — досылаем тот же кадр ещё несколько раз: каждый повтор кодировщик делает чётче,
    // и мелкий текст дотягивается до полного качества даже на скромном битрейте.
    const int MaxRefines = 6;
    static readonly long RefineAfterTicks = TimeSpan.FromMilliseconds(150).Ticks;
    int refines = MaxRefines;
    byte[] pointerBuffer = new byte[64 * 64 * 4];

    public int Width => converter.Width;
    public int Height => converter.Height;
    public CursorPosition Cursor { get; private set; }
    public CursorShape? Shape { get; private set; }

    public DxgiVideo(MonitorInfo monitor, int maxWidth, int fps, int bitrate)
    {
        minFrameTicks = TimeSpan.TicksPerSecond / Math.Clamp(fps, 1, 60);
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var (adapter, output) = FindOutput(factory, monitor.Id)
            ?? throw new VideoUnavailableException("Монитор не найден в DXGI: " + monitor.Id);
        using (adapter)
        using (output)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out device!, out context!).CheckError();
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            duplication = output1.DuplicateOutput(device);
        }
        var desc = duplication.Description;
        sourceWidth = (int)desc.ModeDescription.Width;
        sourceHeight = (int)desc.ModeDescription.Height;

        try
        {
            converter = new Nv12Converter(device, context, sourceWidth, sourceHeight, maxWidth, Math.Clamp(fps, 1, 60));
            encoder = new H264Encoder(Width, Height, Math.Clamp(fps, 1, 60), bitrate);
            AgentLog.Write($"видео: {encoder.Name}, {sourceWidth}×{sourceHeight} → {Width}×{Height}, {fps} кадров/с, {bitrate / 1000} кбит/с");
        }
        catch
        {
            // Захват монитора, оставленный открытым, помешал бы следующей попытке.
            converter?.Dispose();
            duplication.Dispose();
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    static (IDXGIAdapter1, IDXGIOutput)? FindOutput(IDXGIFactory1 factory, string deviceName)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return (adapter, output);
                output.Dispose();
            }
            adapter.Dispose();
        }
        return null;
    }

    public void RequestKeyFrame() => keyWanted = true;

    public VideoFrame? Next(int timeoutMs)
    {
        // Кадр отложен из-за ограничения частоты — ждём не дольше, чем до его очереди.
        var wakeAt = pending ? lastFrameAt + minFrameTicks : refines < MaxRefines ? lastFrameAt + RefineAfterTicks : long.MaxValue;
        var wait = wakeAt == long.MaxValue ? timeoutMs : (int)Math.Clamp((wakeAt - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond, 1, timeoutMs);
        var result = duplication.AcquireNextFrame((uint)wait, out var info, out var resource);
        if (result.Code != WaitTimeout)
        {
            // Смена разрешения, экран UAC, полноэкранная игра со своим режимом — пересоздадим позже.
            result.CheckError();
            try
            {
                if (info.LastMouseUpdateTime != 0)
                    UpdateCursor(info);
                if (info.LastPresentTime != 0)
                {
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    converter.CopyFrom(texture);
                    pending = hasFrame = true;
                    refines = 0;
                }
            }
            finally
            {
                resource.Dispose();
                duplication.ReleaseFrame();
            }
        }

        // Не чаще, чем просили; последнее изменение не теряется — уйдёт, как только подойдёт очередь.
        // Ключевой кадр по просьбе телефона — сразу, даже если экран не менялся.
        var since = DateTime.UtcNow.Ticks - lastFrameAt;
        var refine = !pending && refines < MaxRefines && since >= RefineAfterTicks;
        if (!hasFrame || !(pending && since >= minFrameTicks || keyWanted || refine))
            return null;
        var frame = pending || keyWanted ? converter.Convert() : converter.Frame;
        if (!pending)
            refines++;
        pending = false;
        lastFrameAt = DateTime.UtcNow.Ticks;
        if (keyWanted)
        {
            keyWanted = false;
            encoder.RequestKeyFrame();
        }
        var frames = encoder.Encode(frame);
        if (frames.Count == 0)
            return null;
        if (frames.Count == 1)
            return frames[0];
        // Редко кодировщик отдаёт два кадра разом — склеиваем, телефон разберёт по единицам NAL.
        return new VideoFrame([.. frames.SelectMany(f => f.Data)], frames.Any(f => f.Key));
    }

    void UpdateCursor(OutduplFrameInfo info)
    {
        var p = info.PointerPosition;
        Cursor = new CursorPosition((double)p.Position.X / sourceWidth, (double)p.Position.Y / sourceHeight, p.Visible);
        if (info.PointerShapeBufferSize == 0)
            return;
        if (pointerBuffer.Length < info.PointerShapeBufferSize)
            pointerBuffer = new byte[info.PointerShapeBufferSize];
        var handle = GCHandle.Alloc(pointerBuffer, GCHandleType.Pinned);
        try
        {
            if (duplication.GetFramePointerShape((uint)pointerBuffer.Length, handle.AddrOfPinnedObject(), out _, out var shape).Failure)
                return;
            if (CursorPng(pointerBuffer, shape) is { } png)
                Shape = new CursorShape(png, shape.HotSpot.X, shape.HotSpot.Y, (int)shape.Width,
                    (int)(shape.Type == 1 ? shape.Height / 2 : shape.Height));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Курсор в PNG. Цветной — как есть; монохромный (две маски) и «маскированный цветной» — приблизительно:
    /// инвертирующие точки (I-образный курсор над текстом) рисуем чёрными с белой обводкой не делаем — просто чёрными.
    /// </summary>
    static byte[]? CursorPng(byte[] data, OutduplPointerShapeInfo shape)
    {
        var w = (int)shape.Width;
        var h = (int)(shape.Type == 1 ? shape.Height / 2 : shape.Height);
        var pitch = (int)shape.Pitch;
        if (w <= 0 || h <= 0 || w > 256 || h > 256)
            return null;
        var argb = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int color;
                switch (shape.Type)
                {
                    case 1: // монохромный: AND-маска сверху, XOR-маска снизу
                    {
                        var and = (data[y * pitch + x / 8] >> (7 - x % 8)) & 1;
                        var xor = (data[(y + h) * pitch + x / 8] >> (7 - x % 8)) & 1;
                        color = (and, xor) switch
                        {
                            (0, 0) => unchecked((int)0xFF000000),
                            (0, 1) => unchecked((int)0xFFFFFFFF),
                            (1, 0) => 0,
                            _ => unchecked((int)0xFF000000), // инверсия
                        };
                        break;
                    }
                    case 2: // цветной с альфой
                        color = BitConverter.ToInt32(data, y * pitch + x * 4);
                        break;
                    default: // 4 — маскированный цветной: альфа 0 — непрозрачно, 0xFF — XOR с экраном
                    {
                        var v = BitConverter.ToInt32(data, y * pitch + x * 4);
                        var rgb = v & 0xFFFFFF;
                        color = (v >>> 24) == 0 ? unchecked((int)0xFF000000) | rgb : rgb == 0 ? 0 : unchecked((int)0xFF000000);
                        break;
                    }
                }
                argb[y * w + x] = color;
            }
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy(argb, y * w, bits.Scan0 + y * bits.Stride, w);
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public void Dispose()
    {
        encoder.Dispose();
        converter.Dispose();
        duplication.Dispose();
        context.Dispose();
        device.Dispose();
    }
}
