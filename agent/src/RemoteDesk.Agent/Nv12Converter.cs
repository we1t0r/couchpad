using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk.Agent;

/// <summary>
/// Кадр BGRA с видеокарты → NV12 нужного размера в памяти: уменьшает и переводит цвета видеопроцессор
/// видеокарты (быстро и без нагрузки на процессор), в память копируется уже маленький кадр NV12.
/// Цвета: вход RGB 0–255, выход BT.709 16–235 — так же помечено в видео.
/// </summary>
sealed class Nv12Converter : IDisposable
{
    readonly ID3D11DeviceContext context;
    readonly ID3D11VideoDevice videoDevice;
    readonly ID3D11VideoContext videoContext;
    readonly ID3D11VideoProcessorEnumerator processorEnum;
    readonly ID3D11VideoProcessor processor;
    readonly ID3D11Texture2D source, nv12, staging;
    readonly ID3D11VideoProcessorInputView inputView;
    readonly ID3D11VideoProcessorOutputView outputView;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Кадр NV12: яркость Width×Height, за ней цвет Width×Height/2.</summary>
    public byte[] Frame { get; }

    public Nv12Converter(ID3D11Device device, ID3D11DeviceContext context, int sourceWidth, int sourceHeight, int maxWidth, int fps)
    {
        this.context = context;
        // Не шире, чем просит телефон, и чётный размер — так требует NV12.
        var scale = Math.Min(1.0, (double)maxWidth / sourceWidth);
        Width = Math.Max(2, (int)(sourceWidth * scale) & ~1);
        Height = Math.Max(2, (int)(sourceHeight * scale) & ~1);
        Frame = new byte[Width * Height * 3 / 2];

        source = device.CreateTexture2D(Texture(sourceWidth, sourceHeight, Format.B8G8R8A8_UNorm, ResourceUsage.Default,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        nv12 = device.CreateTexture2D(Texture(Width, Height, Format.NV12, ResourceUsage.Default, BindFlags.RenderTarget));
        var stagingDesc = Texture(Width, Height, Format.NV12, ResourceUsage.Staging, BindFlags.None);
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        staging = device.CreateTexture2D(stagingDesc);

        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        videoContext = context.QueryInterface<ID3D11VideoContext>();
        processorEnum = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)sourceWidth, InputHeight = (uint)sourceHeight,
            OutputWidth = (uint)Width, OutputHeight = (uint)Height,
            InputFrameRate = new Rational((uint)fps, 1), OutputFrameRate = new Rational((uint)fps, 1),
            Usage = VideoUsage.OptimalQuality,
        });
        processor = videoDevice.CreateVideoProcessor(processorEnum, 0);
        inputView = videoDevice.CreateVideoProcessorInputView(source, processorEnum, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        outputView = videoDevice.CreateVideoProcessorOutputView(nv12, processorEnum, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        });
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        videoContext.VideoProcessorSetOutputColorSpace(processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
    }

    static Texture2DDescription Texture(int width, int height, Format format, ResourceUsage usage, BindFlags bind) => new()
    {
        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
        SampleDescription = new SampleDescription(1, 0), Usage = usage, BindFlags = bind,
    };

    /// <summary>Скопировать кадр BGRA (того размера, что задан при создании) — дальше вызвать <see cref="Convert"/>.</summary>
    public void CopyFrom(ID3D11Texture2D bgra) => context.CopyResource(source, bgra);

    /// <summary>Перевести последний скопированный кадр в NV12 — результат в <see cref="Frame"/>.</summary>
    public byte[] Convert()
    {
        videoContext.VideoProcessorBlt(processor, outputView, 0, 1,
            [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
        context.CopyResource(staging, nv12);
        var map = context.Map(staging, 0, MapMode.Read);
        try
        {
            var pitch = (int)map.RowPitch;
            for (var y = 0; y < Height; y++)
                Marshal.Copy(map.DataPointer + y * pitch, Frame, y * Width, Width);
            // В NV12 плоскость цвета идёт сразу после яркости, с тем же шагом строки.
            var chroma = map.DataPointer + pitch * Height;
            for (var y = 0; y < Height / 2; y++)
                Marshal.Copy(chroma + y * pitch, Frame, Width * Height + y * Width, Width);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
        return Frame;
    }

    public void Dispose()
    {
        outputView.Dispose();
        inputView.Dispose();
        processor.Dispose();
        processorEnum.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        staging.Dispose();
        nv12.Dispose();
        source.Dispose();
    }
}
