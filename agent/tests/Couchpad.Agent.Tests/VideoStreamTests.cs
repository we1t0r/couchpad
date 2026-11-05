using System.Collections.Concurrent;
using Couchpad.Agent.Core;

namespace Couchpad.Agent.Tests;

public class VideoStreamTests
{
    sealed class BrokenVideo : IVideoSource
    {
        public int Opened;

        public IVideoCapture OpenVideo(MonitorInfo monitor, int maxWidth, int fps, int bitrate)
        {
            Opened++;
            throw new VideoUnavailableException("Компьютер заблокирован");
        }
    }

    static async Task<(List<byte[]> Binary, List<string> Text)> Run(IVideoSource? video, Action<ScreenStream>? during = null)
    {
        var source = new SyntheticScreen();
        var binary = new ConcurrentQueue<byte[]>();
        var text = new ConcurrentQueue<string>();
        using var stream = new ScreenStream(source.Open(null), d => { binary.Enqueue(d); return Task.CompletedTask; }, 1280, 70, 30,
            video is null ? null : new VideoRequest(video, 30, 4_000_000), t => { text.Enqueue(t); return Task.CompletedTask; });
        stream.Start();
        for (var i = 0; i < 40 && binary.Count < 3; i++)
        {
            await Task.Delay(50);
            stream.Ack(); // телефон подтверждает кадры
        }
        during?.Invoke(stream);
        for (var i = 0; i < 6; i++)
        {
            stream.Ack();
            await Task.Delay(50);
        }
        return ([.. binary], [.. text]);
    }

    [Fact]
    public async Task Video_starts_with_key_frame_and_sends_cursor()
    {
        var (binary, text) = await Run(new SyntheticScreen());
        Assert.NotEmpty(binary);
        Assert.All(binary, f => Assert.Equal(ScreenStream.VideoFrameType, f[0]));
        Assert.Equal(1, binary[0][9]); // первый кадр — ключевой
        Assert.Equal(16, binary[0][6]); // ширина 16
        Assert.Contains(text, t => t.Contains("\"cursor_shape\""));
        Assert.Contains(text, t => t.Contains("\"cursor\"") && t.Contains("\"v\":true"));
    }

    [Fact]
    public async Task Key_frame_on_request()
    {
        var (binary, _) = await Run(new SyntheticScreen(), s => s.RequestKeyFrame());
        Assert.True(binary.Count(f => f[9] == 1) >= 2);
    }

    [Fact]
    public async Task Falls_back_to_jpeg_when_video_is_unavailable()
    {
        var broken = new BrokenVideo();
        var (binary, _) = await Run(broken);
        Assert.NotEmpty(binary);
        Assert.Equal(ScreenStream.FrameType, binary[0][0]);
        Assert.True(broken.Opened >= 1);
    }
}
