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

    /// <summary>
    /// LiveKit server URL, e.g. wss://livekit.example.com. Falls back to <c>LiveKit:Url</c>
    /// — see <see cref="VoiceLiveKitCredentials"/>, the single credential source shared with
    /// the browser token endpoint.
    /// </summary>
    public string LiveKitUrl { get; set; } = string.Empty;

    /// <summary>
    /// LiveKit API key/secret used to mint the agent's join token. Falls back to
    /// <c>LiveKit:ApiKey</c> / <c>LiveKit:ApiSecret</c> via
    /// <see cref="VoiceLiveKitCredentials.Resolve"/>, so configuring EITHER block configures
    /// both the endpoint and the agent.
    /// </summary>
    public string LiveKitApiKey { get; set; } = string.Empty;
    public string LiveKitApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// Room LABEL (NOT the full room name). The real room name is always
    /// <c>{tenant:N}-{user:N}-{label}</c>, produced by <see cref="VoiceRoomNaming"/> — the
    /// same function the browser token endpoint uses — so the agent and its caller land in
    /// the same room and two users in one tenant never share one.
    /// </summary>
    public string Room { get; set; } = VoiceRoomNaming.DefaultLabel;

    /// <summary>Participant identity the agent publishes under.</summary>
    public string AgentIdentity { get; set; } = "voice-agent";

    // ── Which user's room the single hosted agent serves ──
    // The hosted service is ONE background participant for the whole process, so it can only
    // occupy ONE room. Because rooms are now scoped per user, the operator must name the
    // tenant/user whose room that is. Empty (the default) ⇒ the hosted service stands down
    // with a clear message rather than silently joining a room no caller will ever be in.
    // Serving many users concurrently needs a room-dispatcher redesign — see
    // VOICE-CU-FIX-INTEGRATION.md.

    /// <summary>Tenant id (GUID) whose voice room the hosted agent joins. Empty ⇒ stand down.</summary>
    public string AgentTenantId { get; set; } = string.Empty;

    /// <summary>User id (GUID) whose voice room the hosted agent joins. Empty ⇒ stand down.</summary>
    public string AgentUserId { get; set; } = string.Empty;

    /// <summary>
    /// Resolves the FULL room name the hosted agent must join, using the same
    /// <see cref="VoiceRoomNaming"/> function as the token endpoint. Returns false with a
    /// human-readable reason when <see cref="AgentTenantId"/>/<see cref="AgentUserId"/> are
    /// missing or malformed, or the label sanitizes to nothing.
    /// </summary>
    public bool TryResolveAgentRoom(out string room, out string? error)
    {
        room = string.Empty;

        if (!Guid.TryParse(AgentTenantId, out var tenantId) || tenantId == Guid.Empty)
        {
            error = "Voice:AgentTenantId must be a non-empty GUID naming the tenant whose room the agent joins.";
            return false;
        }
        if (!Guid.TryParse(AgentUserId, out var userId) || userId == Guid.Empty)
        {
            error = "Voice:AgentUserId must be a non-empty GUID naming the user whose room the agent joins.";
            return false;
        }

        return VoiceRoomNaming.TryScopedRoom(tenantId, userId, Room, out room, out error);
    }
}
