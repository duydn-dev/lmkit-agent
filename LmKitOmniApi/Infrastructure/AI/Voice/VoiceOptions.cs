namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Configuration for the voice "Live" groundwork. Bound from the "Voice" section.
///
/// EVERYTHING IS OFF BY DEFAULT — the same posture as <c>DatabaseAgentOptions</c>.
/// LM-Kit.NET (2026.8.2) ships no offline text-to-speech engine, so
/// <see cref="TtsEnabled"/> alone does nothing useful: an <c>ISpeechSynthesizer</c>
/// implementation must also be registered before <c>/api/speech/synthesize</c> can
/// return audio. Likewise the real-time room agent needs a live LiveKit connection
/// plus a capable model, so <see cref="LiveAgentEnabled"/> gates a hosted service
/// that is a strict NO-OP until an operator turns it on.
/// </summary>
public sealed class VoiceOptions
{
    public const string SectionName = "Voice";

    /// <summary>
    /// Master switch for text-to-speech. False (default) = the synthesize endpoint
    /// always reports "engine not configured" (HTTP 501). Even when true, audio is
    /// only produced if an <c>ISpeechSynthesizer</c> is registered and reports
    /// <c>IsAvailable</c>; there is no built-in engine.
    /// </summary>
    public bool TtsEnabled { get; set; }

    /// <summary>
    /// Master switch for the real-time voice room agent hosted service. False
    /// (default) = the hosted service does nothing. When true it still refuses to
    /// join a room in this build because the LiveKit media join is a live-only stub.
    /// </summary>
    public bool LiveAgentEnabled { get; set; }

    /// <summary>Voice/preset name passed to the synthesizer when the caller omits one.</summary>
    public string DefaultVoice { get; set; } = "default";

    /// <summary>Hard cap on the number of characters accepted by a single synthesis request.</summary>
    public int MaxSynthesisCharacters { get; set; } = 2000;

    // ── Piper local TTS engine (the registered ISpeechSynthesizer) ──
    // Piper is offline/on-prem, so it keeps the local-first posture. All empty by
    // default → the engine reports IsAvailable=false and the endpoint stays 501 until
    // an operator installs the binary + a voice model and points these at them.

    /// <summary>Path to the Piper executable (a full path, or a name resolvable on PATH like "piper").</summary>
    public string? PiperExecutablePath { get; set; }

    /// <summary>Map of voice name → Piper ONNX voice-model file path (e.g. "vi" → "/models/vi_VN.onnx").</summary>
    public Dictionary<string, string> PiperVoices { get; set; } = new();

    /// <summary>Wall-clock budget for one Piper synthesis run.</summary>
    public int SynthesisTimeoutSeconds { get; set; } = 30;

    // ── LiveKit real-time room agent (used only when LiveAgentEnabled) ──
    // All empty by default → the hosted service logs "not configured" and stands down.

    /// <summary>LiveKit server URL, e.g. wss://livekit.example.com.</summary>
    public string LiveKitUrl { get; set; } = string.Empty;

    /// <summary>LiveKit API key/secret used to mint the agent's join token.</summary>
    public string LiveKitApiKey { get; set; } = string.Empty;
    public string LiveKitApiSecret { get; set; } = string.Empty;

    /// <summary>Room the agent joins, and the identity/voice it uses.</summary>
    public string Room { get; set; } = "omni-room";
    public string AgentIdentity { get; set; } = "voice-agent";
}
