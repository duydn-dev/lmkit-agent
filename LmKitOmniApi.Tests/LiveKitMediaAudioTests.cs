using System.Text;
using LmKitOmniApi.Infrastructure.AI.Voice;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure audio-math for the LiveKit voice media session — the correctness-sensitive parts
/// (WAV parsing + down-mix/resample) that decide whether the agent hears/speaks intelligibly.
/// These run in CI without LiveKit, the native runtime, or any audio device (the real
/// join/frame loop is live-only). A silent bug here corrupts every utterance, so it's worth
/// pinning down.
/// </summary>
public sealed class LiveKitMediaAudioTests
{
    [Fact]
    public void ParseWav_RoundTripsMono16k()
    {
        var samples = new short[] { 0, 100, -100, 32000, -32000, 5 };
        var (pcm, rate, channels) = LiveKitMediaSession.ParseWav(BuildWav(samples, 16000, 1));

        Assert.Equal(16000, rate);
        Assert.Equal(1, channels);
        Assert.Equal(samples, pcm);
    }

    [Fact]
    public void ParseWav_ReadsStereoRateAndChannels()
    {
        var samples = new short[] { 1, 2, 3, 4 }; // 2 stereo frames
        var (pcm, rate, channels) = LiveKitMediaSession.ParseWav(BuildWav(samples, 8000, 2));

        Assert.Equal(8000, rate);
        Assert.Equal(2, channels);
        Assert.Equal(4, pcm.Length);
    }

    [Fact]
    public void Resample_SameRateMono_IsIdentity()
    {
        var pcm = new short[] { 10, 20, 30, 40 };
        Assert.Equal(pcm, LiveKitMediaSession.Resample(pcm, 1, 16000, 16000));
    }

    [Fact]
    public void Resample_Upsample_RoughlyDoublesLength()
    {
        var pcm = new short[] { 0, 100, 200, 300 };
        var outBuf = LiveKitMediaSession.Resample(pcm, 1, 8000, 16000);
        Assert.Equal(8, outBuf.Length); // 4 * 16000/8000
    }

    [Fact]
    public void Resample_DownmixesStereoToMono()
    {
        // Interleaved L,R: (10,20),(30,50) → mono averages: 15, 40.
        var stereo = new short[] { 10, 20, 30, 50 };
        var mono = LiveKitMediaSession.Resample(stereo, 2, 16000, 16000);
        Assert.Equal(new short[] { 15, 40 }, mono);
    }

    private static byte[] BuildWav(short[] samples, int rate, int channels)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);   // byte rate
        w.Write((short)(channels * 2)); // block align
        w.Write((short)16);             // bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
