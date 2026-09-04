namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// A single real-time voice "turn": one inbound utterance in, one spoken reply out.
/// </summary>
public sealed class VoiceTurnContext
{
    /// <summary>The captured inbound utterance as WAV/PCM bytes (a completed utterance, not a live frame).</summary>
    public ReadOnlyMemory<byte> InboundAudio { get; init; }

    /// <summary>Voice/preset name to speak the reply with.</summary>
    public string Voice { get; init; } = "default";
}

/// <summary>Outcome of one voice turn.</summary>
public sealed class VoiceTurnResult
{
    /// <summary>True when a spoken reply was produced (STT and LLM both yielded text).</summary>
    public bool Handled { get; init; }

    /// <summary>True when the turn was skipped (silence / empty transcript / empty reply).</summary>
    public bool Skipped { get; init; }

    /// <summary>Why the turn was skipped, for diagnostics.</summary>
    public string? SkipReason { get; init; }

    public string UserText { get; init; } = string.Empty;
    public string ReplyText { get; init; } = string.Empty;

    /// <summary>WAV bytes of the spoken reply; empty when skipped or no TTS engine is available.</summary>
    public byte[] ReplyAudio { get; init; } = Array.Empty<byte>();

    public static VoiceTurnResult Empty(string reason, string userText = "") =>
        new() { Skipped = true, SkipReason = reason, UserText = userText };

    public static VoiceTurnResult Reply(string userText, string replyText, byte[] audio) =>
        new() { Handled = true, UserText = userText, ReplyText = replyText, ReplyAudio = audio };
}

/// <summary>
/// Orchestrates the STT → LLM → TTS turn loop for a real-time voice room agent.
/// The orchestration in <see cref="VoiceRoomAgent"/> is pure and unit-testable with fakes;
/// the actual LiveKit media transport (see <see cref="ILiveKitMediaSession"/>) is a live-only
/// concern kept out of this method so a turn can be exercised without any audio hardware.
/// </summary>
public interface IVoiceRoomAgent
{
    Task<VoiceTurnResult> RunTurnAsync(VoiceTurnContext context, CancellationToken ct = default);
}

/// <summary>Speech-to-text step of a voice turn (audio bytes → transcript).</summary>
public interface IVoiceTurnStt
{
    Task<string> TranscribeAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default);
}

/// <summary>Language-model step of a voice turn (user utterance → reply text).</summary>
public interface IVoiceTurnLlm
{
    Task<string> RespondAsync(string userUtterance, CancellationToken ct = default);
}
