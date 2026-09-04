namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Pluggable text-to-speech engine seam.
///
/// LM-Kit.NET (2026.8.2) exposes speech-to-text only (<c>LMKit.Speech.SpeechToText</c>)
/// and has NO speech-synthesis API, so this project ships NO implementation of this
/// interface by default. That is deliberate: the synthesize command/endpoint is a real,
/// frozen contract, but until a real engine is registered (e.g. a future LM-Kit release,
/// an ONNX vocoder, or an external service adapter) every request reports
/// "engine not configured" instead of returning fake audio.
///
/// A real implementation would produce a complete, self-contained WAV byte payload.
/// <c>LMKit.Media.Audio.WaveFile</c> can build one from normalized mono float samples
/// (its <c>WaveFile(float[], uint)</c> constructor plus <c>SaveAsMono16k(Stream)</c>),
/// which is the natural bridge for an engine that emits PCM samples.
/// </summary>
public interface ISpeechSynthesizer
{
    /// <summary>
    /// True when this engine can actually synthesize audio right now. An engine that is
    /// wired but missing its model/native assets should return false so callers fall back
    /// to the "not configured" path rather than throwing mid-request.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Synthesizes <paramref name="text"/> into a complete WAV (RIFF) byte payload.
    /// </summary>
    /// <param name="text">The text to speak. Callers validate non-empty/length first.</param>
    /// <param name="voice">Engine-specific voice/preset name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>WAV-encoded audio bytes (<c>audio/wav</c>).</returns>
    Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default);
}
