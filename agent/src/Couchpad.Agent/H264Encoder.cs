using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Couchpad.Agent;

/// <summary>
/// Сжатие кадров NV12 в H.264 кодировщиком Windows (Media Foundation). Сначала пробуем кодировщик видеокарты
/// (NVENC, Quick Sync, AMF — они асинхронные), не вышло — программный от Microsoft. Режим малой задержки:
/// без B-кадров, кадр на входе — кадр на выходе.
/// </summary>
sealed class H264Encoder : IDisposable
{
    const int MftEnumHardware = 0x4, MftEnumSync = 0x1, MftEnumSortAndFilter = 0x40;
    const int EventFlagNoWait = 1;
    const int TransformNeedInput = 601, TransformHaveOutput = 602;
    const int ProvidesSamples = 0x100, CanProvideSamples = 0x200;
    static readonly int NeedMoreInput = unchecked((int)0xC00D6D72);
    static readonly int StreamChange = unchecked((int)0xC00D6D61);
    static readonly int NoEvents = unchecked((int)0xC00D3E80);

    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    // ICodecAPI: режим битрейта, размер группы кадров, B-кадры, «следующий кадр ключевой».
    static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    static readonly Guid MeanBitrate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    static readonly Guid BPictures = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    readonly IMFTransform mft;
    readonly IMFMediaEventGenerator? events;
    readonly ICodecApi? codec;
    readonly int width, height;
    readonly long frameDuration;
    readonly bool providesSamples;
    readonly int outputSize;
    int inputCredits;
    long time;
    byte[]? sequenceHeader;

    /// <summary>Имя кодировщика — для журнала («NVIDIA H.264 Encoder MFT»).</summary>
    public string Name { get; }

    public H264Encoder(int width, int height, int fps, int bitrate)
    {
        this.width = width;
        this.height = height;
        frameDuration = 10_000_000L / fps;
        MediaFactory.MFStartup().CheckError();

        Exception? failure = null;
        foreach (var flags in new[] { MftEnumHardware | MftEnumSortAndFilter, MftEnumSync | MftEnumSortAndFilter })
        {
            using var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags,
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
            foreach (var activate in found)
            {
                IMFTransform? candidate = null;
                try
                {
                    candidate = activate.ActivateObject<IMFTransform>();
                    var name = TryString(activate, FriendlyName) ?? "H.264";
                    var isAsync = TryUInt(candidate.Attributes, TransformAsync) == 1;
                    if (isAsync)
                        candidate.Attributes.Set(TransformAsyncUnlock, 1u).CheckError();
                    Configure(candidate, fps, bitrate, out var codecApi);
                    mft = candidate;
                    codec = codecApi;
                    events = isAsync ? candidate.QueryInterface<IMFMediaEventGenerator>() : null;
                    var info = candidate.GetOutputStreamInfo(0);
                    providesSamples = (info.Flags & (ProvidesSamples | CanProvideSamples)) != 0;
                    outputSize = Math.Max(info.Size, width * height);
                    Name = name;
                    mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
                    mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
                    return;
                }
                catch (Exception e)
                {
                    failure = e;
                    candidate?.Dispose();
                }
            }
        }
        throw new InvalidOperationException("Нет подходящего кодировщика H.264", failure);
    }

    void Configure(IMFTransform transform, int fps, int bitrate, out ICodecApi? codecApi)
    {
        codecApi = null;
        try
        {
            codecApi = (ICodecApi)Marshal.GetObjectForIUnknown(transform.NativePointer);
            codecApi.Set(RateControlMode, 0u); // постоянный битрейт: предсказуемо для Wi-Fi и мобильного интернета
            codecApi.Set(MeanBitrate, (uint)bitrate);
            codecApi.Set(GopSize, (uint)(fps * 4)); // ключевой кадр раз в 4 с — картинка восстановится сама
            codecApi.Set(BPictures, 0u);
            codecApi.Set(LowLatency, true);
        }
        catch (Exception)
        {
            // Не у всех кодировщиков есть ICodecAPI — тогда как получится по умолчанию.
        }
        try
        {
            transform.Attributes.Set(LowLatency, 1u);
        }
        catch (Exception)
        {
        }

        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MajorType, MediaTypeGuids.Video).CheckError();
        output.Set(Subtype, VideoFormatGuids.H264).CheckError();
        output.Set(AvgBitrate, (uint)bitrate).CheckError();
        output.Set(InterlaceMode, 2u).CheckError(); // построчная развёртка
        output.Set(FrameSize, MediaFactory.PackSize((uint)width, (uint)height)).CheckError();
        output.Set(FrameRate, MediaFactory.Pack2UInt32AsUInt64((uint)fps, 1)).CheckError();
        output.Set(PixelAspect, MediaFactory.Pack2UInt32AsUInt64(1, 1)).CheckError();
        output.Set(Profile, 77u).CheckError(); // Main: умеют все телефоны
        output.Set(YuvMatrix, 1u).CheckError(); // BT.709
        output.Set(NominalRange, 2u).CheckError(); // 16–235
        transform.SetOutputType(0, output, 0);

        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MajorType, MediaTypeGuids.Video).CheckError();
        input.Set(Subtype, VideoFormatGuids.NV12).CheckError();
        input.Set(InterlaceMode, 2u).CheckError();
        input.Set(FrameSize, MediaFactory.PackSize((uint)width, (uint)height)).CheckError();
        input.Set(FrameRate, MediaFactory.Pack2UInt32AsUInt64((uint)fps, 1)).CheckError();
        input.Set(PixelAspect, MediaFactory.Pack2UInt32AsUInt64(1, 1)).CheckError();
        input.Set(YuvMatrix, 1u).CheckError();
        input.Set(NominalRange, 2u).CheckError();
        transform.SetInputType(0, input, 0);
    }

    /// <summary>Следующий кадр сжать как ключевой.</summary>
    public void RequestKeyFrame()
    {
        try
        {
            codec?.Set(ForceKeyFrame, 1u);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Сжать кадр NV12 (<paramref name="nv12"/>: яркость width×height, затем цвет width×height/2).
    /// Возвращает готовые кадры H.264 — обычно один.
    /// </summary>
    public List<Core.VideoFrame> Encode(ReadOnlySpan<byte> nv12)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        buffer.Lock(out var pointer, out _, out _);
        unsafe
        {
            nv12.CopyTo(new Span<byte>((void*)pointer, nv12.Length));
        }
        buffer.Unlock();
        buffer.CurrentLength = nv12.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = frameDuration;
        time += frameDuration;

        var frames = new List<Core.VideoFrame>();
        if (events is null)
        {
            mft.ProcessInput(0, sample, 0);
            while (Pull(frames))
            {
            }
            return frames;
        }

        // Асинхронный кодировщик сам говорит, когда ждёт кадр и когда готов результат.
        while (inputCredits == 0)
            Handle(events.GetEvent(0), frames);
        mft.ProcessInput(0, sample, 0);
        inputCredits--;
        // Ждём, пока кодировщик отдаст кадр или попросит следующий, — так задержка не копится.
        var gotOutput = false;
        while (!gotOutput && inputCredits == 0)
            gotOutput = Handle(events.GetEvent(0), frames);
        while (TryEvent() is { } e)
            Handle(e, frames);
        return frames;
    }

    IMFMediaEvent? TryEvent()
    {
        try
        {
            return events!.GetEvent(EventFlagNoWait);
        }
        catch (SharpGenException e) when (e.HResult == NoEvents)
        {
            return null;
        }
    }

    /// <returns>true — пришёл готовый кадр.</returns>
    bool Handle(IMFMediaEvent e, List<Core.VideoFrame> frames)
    {
        using (e)
        {
            switch ((int)e.EventType)
            {
                case TransformNeedInput:
                    inputCredits++;
                    return false;
                case TransformHaveOutput:
                    Pull(frames);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <returns>true — кадр забран, можно пробовать ещё.</returns>
    bool Pull(List<Core.VideoFrame> frames)
    {
        IMFSample? own = null;
        if (!providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            own.AddBuffer(MediaFactory.MFCreateMemoryBuffer(outputSize));
        }
        var data = new OutputDataBuffer { StreamID = 0, Sample = own! };
        var result = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
        try
        {
            if (result.Code == NeedMoreInput)
                return false;
            if (result.Code == StreamChange)
            {
                // Кодировщик уточнил формат выхода — принимаем его и продолжаем.
                using var type = mft.GetOutputAvailableType(0, 0);
                mft.SetOutputType(0, type, 0);
                ReadSequenceHeader();
                return true;
            }
            result.CheckError();
            var sample = data.Sample;
            if (sample is null)
                return false;
            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var pointer, out _, out var length);
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            contiguous.Unlock();
            var key = TryUInt(sample, CleanPoint) == 1;
            if (key)
            {
                sequenceHeader ??= ReadSequenceHeader();
                // Телефону нужны SPS и PPS перед ключевым кадром; не все кодировщики кладут их сами.
                if (sequenceHeader is { Length: > 0 } header && !HasNal(bytes, 7))
                    bytes = [.. header, .. bytes];
            }
            frames.Add(new Core.VideoFrame(bytes, key));
            return true;
        }
        finally
        {
            data.Sample?.Dispose();
            data.Events?.Dispose();
            if (!ReferenceEquals(own, data.Sample))
                own?.Dispose();
        }
    }

    byte[]? ReadSequenceHeader()
    {
        try
        {
            using var type = mft.GetOutputCurrentType(0);
            return sequenceHeader = type.GetBlob(SequenceHeader);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Есть ли в Annex B единица NAL такого типа (7 — SPS).</summary>
    static bool HasNal(byte[] data, int type)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1f) == type)
                return true;
        return false;
    }

    static uint? TryUInt(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetUInt32(key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string? TryString(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (Exception)
        {
        }
        if (codec is not null)
            Marshal.ReleaseComObject(codec);
        events?.Dispose();
        mft.Dispose();
    }

}

/// <summary>ICodecAPI (strmif.h) — только нужные методы, по порядку в таблице.</summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICodecApi
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, IntPtr min, IntPtr max, IntPtr step);
    [PreserveSig] int GetParameterValues(in Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(in Guid api, IntPtr value);
    [PreserveSig] int GetValue(in Guid api, IntPtr value);
    [PreserveSig] int SetValue(in Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
}

static class CodecApiExtensions
{
    /// <summary>Значение настройки кодировщика (uint — VT_UI4, bool — VT_BOOL). Отказ — исключение.</summary>
    internal static void Set(this ICodecApi codec, Guid api, object value)
    {
        var hr = codec.SetValue(api, ref value);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }
}
