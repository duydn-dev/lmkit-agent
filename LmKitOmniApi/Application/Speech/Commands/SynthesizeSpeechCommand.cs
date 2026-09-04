using MediatR;

namespace LmKitOmniApi.Application.Speech.Commands;

/// <summary>
/// Text-to-speech request. Backs <c>POST /api/speech/synthesize</c>.
/// </summary>
public sealed class SynthesizeSpeechCommand : IRequest<SynthesizeSpeechResult>
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Optional engine voice/preset; falls back to <c>VoiceOptions.DefaultVoice</c>.</summary>
    public string? Voice { get; set; }
}

public enum SynthesizeSpeechStatus
{
    /// <summary>Audio was produced.</summary>
    Success,

    /// <summary>No engine is configured/enabled/available — the endpoint should answer 501.</summary>
    EngineNotConfigured
}

public sealed class SynthesizeSpeechResult
{
    public SynthesizeSpeechStatus Status { get; init; }

    /// <summary>Complete WAV bytes when <see cref="Status"/> is <see cref="SynthesizeSpeechStatus.Success"/>.</summary>
    public byte[] Audio { get; init; } = Array.Empty<byte>();

    public string ContentType { get; init; } = "audio/wav";

    /// <summary>Human-readable reason, populated on the not-configured path.</summary>
    public string? Message { get; init; }

    public static SynthesizeSpeechResult Success(byte[] audio, string contentType = "audio/wav") =>
        new() { Status = SynthesizeSpeechStatus.Success, Audio = audio, ContentType = contentType };

    public static SynthesizeSpeechResult NotConfigured(string message) =>
        new() { Status = SynthesizeSpeechStatus.EngineNotConfigured, Message = message };
}
