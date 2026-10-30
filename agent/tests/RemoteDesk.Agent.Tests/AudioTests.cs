using System.Buffers.Binary;
using System.Collections.Concurrent;
using Concentus;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

public class AudioTests
{
    [Fact]
    public async Task Stream_sends_numbered_opus_frames_that_decode_to_sound()
    {
        var frames = new ConcurrentQueue<byte[]>();
        using (new AudioStream(new SyntheticAudio(), data => { frames.Enqueue(data); return Task.CompletedTask; }, muteSpeakers: false, bitrate: 96_000))
        {
            for (var i = 0; i < 100 && frames.Count < 10; i++)
                await Task.Delay(50);
        }
        Assert.True(frames.Count >= 10);

        var decoder = OpusCodecFactory.CreateDecoder(IAudioSource.Rate, 2);
        var pcm = new float[AudioStream.FrameSamples * 2];
        var expected = 1u;
        var loudest = 0f;
        foreach (var frame in frames)
        {
            Assert.Equal(AudioStream.FrameType, frame[0]);
            Assert.Equal(expected++, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1)));
            var samples = decoder.Decode(frame.AsSpan(5), pcm, AudioStream.FrameSamples);
            Assert.Equal(AudioStream.FrameSamples, samples);
            loudest = Math.Max(loudest, pcm.Max(Math.Abs));
        }
        // Тон громкостью 0,2 доходит до телефона, а не тишина.
        Assert.InRange(loudest, 0.1f, 0.4f);
    }

    [Fact]
    public void Null_audio_refuses() =>
        Assert.Throws<InvalidOperationException>(() => new AudioStream(new NullAudio(), _ => Task.CompletedTask, false, 96_000));
}
