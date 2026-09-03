using System.Threading.Channels;
using LiveKit.Rtc;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Real LiveKit media transport for the voice room agent, built on Livekit.Rtc.Dotnet
/// (the .NET binding to LiveKit's Rust client). It connects to a room, endpoints the
/// caller's inbound audio into utterances for STT, and publishes the synthesized reply
/// back as a mono 16 kHz track.
///
/// LIVE-ONLY: this compiles against the real SDK but cannot run in CI — it needs a live
/// LiveKit server, the native livekit_ffi runtime, and a real audio track. It is registered
/// only when <c>Voice:LiveAgentEnabled</c> is true; otherwise the no-op hosted service /
/// stub stands in. The endpointer here is a deliberately simple energy-VAD (speech when the
/// frame RMS crosses a threshold, utterance ends after a silence gap) and the resampler is
/// linear — both are correct-enough to run end-to-end but should be tuned/replaced (e.g. a
/// real VAD) for production quality.
/// </summary>
public sealed class LiveKitMediaSession : ILiveKitMediaSession
{
    private const int SampleRate = 16000; // mono 16 kHz both directions (Whisper-friendly)
    private const int Channels = 1;

    // Energy-VAD endpointer knobs (basic; tune for production).
    private const short SpeechRmsThreshold = 500;   // int16 RMS above this = speech
    private const int SilenceMsToEndUtterance = 700; // trailing silence that closes an utterance
    private const int MinUtteranceMs = 300;          // ignore blips shorter than this
    private const int MaxUtteranceMs = 30000;        // hard cap per utterance

    private readonly ILogger<LiveKitMediaSession> _logger;
    private readonly Channel<byte[]> _utterances = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

    private Room? _room;
    private AudioSource? _source;
    private LocalAudioTrack? _outTrack;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    public LiveKitMediaSession(ILogger<LiveKitMediaSession> logger) => _logger = logger;

    public async Task JoinAsync(VoiceRoomOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Url) || string.IsNullOrWhiteSpace(options.Token))
            throw new InvalidOperationException("LiveKit URL and token are required to join a room.");

        _room = new Room();
        // Auto-subscribe so the caller's mic track is delivered; the first subscribed audio
        // track starts the endpointing reader.
        _room.TrackSubscribed += OnTrackSubscribed;
        await _room.ConnectAsync(options.Url, options.Token, new RoomOptions { AutoSubscribe = true }, ct);

        // Output path: a mono 16 kHz source published as the agent's voice track.
        _source = new AudioSource(SampleRate, Channels, 1000);
        _outTrack = LocalAudioTrack.Create(options.Identity + "-voice", _source);
        await _room.LocalParticipant.PublishTrackAsync(
            _outTrack, new TrackPublishOptions { Source = LiveKit.Proto.TrackSource.SourceMicrophone }, ct);

        _logger.LogInformation("🎙️ [LiveKit] Voice agent joined room '{Room}' as '{Id}'.", options.Room, options.Identity);
    }

    private void OnTrackSubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        // Only start one reader, and only for an audio track.
        if (_readerTask is not null || e.Track is not { } track) return;
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadLoopAsync(track, _readerCts.Token));
        _logger.LogInformation("🎙️ [LiveKit] Subscribed to caller audio; endpointing started.");
    }

    /// <summary>Reads inbound frames, endpoints them into utterances, and queues each as WAV.</summary>
    private async Task ReadLoopAsync(Track track, CancellationToken ct)
    {
        try
        {
            await using var stream = AudioStream.FromTrack(track, SampleRate, Channels, null, 0, null, null);
            var buffer = new List<short>();
            var trailingSilenceMs = 0;
            var inSpeech = false;

            await foreach (var frameEvent in stream.WithCancellation(ct))
            {
                var frame = frameEvent.Frame;
                var samples = ToInt16Samples(frame.DataBytes);
                var frameMs = frame.SamplesPerChannel * 1000 / Math.Max(1, (int)frame.SampleRate);
                var speech = Rms(samples) >= SpeechRmsThreshold;

                if (speech)
                {
                    inSpeech = true;
                    trailingSilenceMs = 0;
                    buffer.AddRange(samples);
                }
                else if (inSpeech)
                {
                    buffer.AddRange(samples); // keep a little trailing silence for natural endpointing
                    trailingSilenceMs += frameMs;
                }

                var utteranceMs = buffer.Count * 1000 / SampleRate;
                if (inSpeech && (trailingSilenceMs >= SilenceMsToEndUtterance || utteranceMs >= MaxUtteranceMs))
                {
                    if (utteranceMs >= MinUtteranceMs)
                        _utterances.Writer.TryWrite(SamplesToWav(buffer.ToArray()));
                    buffer.Clear();
                    inSpeech = false;
                    trailingSilenceMs = 0;
                }
            }
        }
        catch (OperationCanceledException) { /* leaving */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🎙️ [LiveKit] Inbound audio reader stopped.");
        }
        finally
        {
            _utterances.Writer.TryComplete();
        }
    }

    public async Task<ReadOnlyMemory<byte>?> ReadUtteranceAsync(CancellationToken ct = default)
    {
        try
        {
            if (await _utterances.Reader.WaitToReadAsync(ct) && _utterances.Reader.TryRead(out var wav))
                return wav;
            return null; // room closed / reader completed
        }
        catch (OperationCanceledException) { return null; }
    }

    public async Task PublishAsync(ReadOnlyMemory<byte> wavAudio, CancellationToken ct = default)
    {
        if (_source is null) throw new InvalidOperationException("Not joined: no audio source to publish through.");
        var (pcm, rate, channels) = ParseWav(wavAudio.ToArray());
        var mono16k = Resample(pcm, channels, rate, SampleRate);

        // Push in ~20 ms frames so playout is smooth.
        const int frameSamples = SampleRate / 50; // 20 ms
        for (var offset = 0; offset < mono16k.Length; offset += frameSamples)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(frameSamples, mono16k.Length - offset);
            var chunk = new short[count];
            Array.Copy(mono16k, offset, chunk, 0, count);
            var frame = new AudioFrame(chunk, SampleRate, Channels, count);
            await _source.CaptureFrameAsync(frame, ct);
        }
    }

    public async Task LeaveAsync(CancellationToken ct = default)
    {
        try { _readerCts?.Cancel(); } catch { /* ignore */ }
        if (_room is not null)
        {
            _room.TrackSubscribed -= OnTrackSubscribed;
            try { await _room.DisconnectAsync(); } catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveAsync(CancellationToken.None);
        try { _outTrack?.Dispose(); } catch { }
        try { _source?.Dispose(); } catch { }
        try { _room?.Dispose(); } catch { }
        _readerCts?.Dispose();
    }

    // ── audio helpers (int16 mono PCM) ──

    private static short[] ToInt16Samples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    private static short Rms(short[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return (short)Math.Min(short.MaxValue, Math.Sqrt(sum / samples.Length));
    }

    private static byte[] SamplesToWav(short[] samples) =>
        new AudioFrame(samples, SampleRate, Channels, samples.Length).ToWavBytes();

    /// <summary>Minimal RIFF/WAV parser → 16-bit PCM samples + rate + channels.</summary>
    internal static (short[] Pcm, int SampleRate, int Channels) ParseWav(byte[] wav)
    {
        // Locate the "fmt " and "data" chunks rather than assuming a fixed 44-byte header.
        int rate = SampleRate, channels = Channels, bits = 16, dataOffset = -1, dataLen = 0;
        var i = 12; // skip "RIFF"<size>"WAVE"
        while (i + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, i, 4);
            var size = BitConverter.ToInt32(wav, i + 4);
            var body = i + 8;
            if (id == "fmt " && body + 16 <= wav.Length)
            {
                channels = BitConverter.ToInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLen = Math.Min(size, wav.Length - body);
                break;
            }
            i = body + size + (size & 1);
        }
        if (dataOffset < 0 || bits != 16) return (Array.Empty<short>(), rate, channels);
        var pcm = new short[dataLen / 2];
        Buffer.BlockCopy(wav, dataOffset, pcm, 0, pcm.Length * 2);
        return (pcm, rate, Math.Max(1, channels));
    }

    /// <summary>Down-mix to mono and linearly resample to <paramref name="target"/> Hz (basic; tune for prod).</summary>
    internal static short[] Resample(short[] pcm, int channels, int sourceRate, int target)
    {
        if (pcm.Length == 0) return pcm;
        // Down-mix to mono.
        short[] mono;
        if (channels <= 1) mono = pcm;
        else
        {
            mono = new short[pcm.Length / channels];
            for (var n = 0; n < mono.Length; n++)
            {
                int acc = 0;
                for (var c = 0; c < channels; c++) acc += pcm[n * channels + c];
                mono[n] = (short)(acc / channels);
            }
        }
        if (sourceRate == target) return mono;
        var outLen = (int)((long)mono.Length * target / Math.Max(1, sourceRate));
        var outBuf = new short[outLen];
        for (var n = 0; n < outLen; n++)
        {
            var srcPos = (double)n * sourceRate / target;
            var i0 = (int)srcPos;
            var frac = srcPos - i0;
            var a = mono[Math.Min(i0, mono.Length - 1)];
            var b = mono[Math.Min(i0 + 1, mono.Length - 1)];
            outBuf[n] = (short)(a + (b - a) * frac);
        }
        return outBuf;
    }
}
