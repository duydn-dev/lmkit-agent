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
    // Serving many users concurrently needs a room-dispatcher redesign — tracked in
    // LmKitOmniApi/docs/known-issues.md.

    /// <summary>Tenant id (GUID) whose voice room the hosted agent joins. Empty ⇒ stand down.</summary>
    public string AgentTenantId { get; set; } = string.Empty;

    /// <summary>User id (GUID) whose voice room the hosted agent joins. Empty ⇒ stand down.</summary>
    public string AgentUserId { get; set; } = string.Empty;

    // ── Multi-room dispatcher (OFF BY DEFAULT) ──
    // When DispatcherEnabled is false the hosted service behaves EXACTLY as it always has: one
    // process-wide agent in the single room named by AgentTenantId/AgentUserId, or a loud
    // stand-down. Turning it on replaces that with one session per CONSENTING room, bounded by
    // every cap below. LiveAgentEnabled remains the master switch: dispatcher mode does nothing
    // while it is false.

    /// <summary>
    /// Master switch for the bounded multi-room dispatcher. False (default) ⇒ the shipped
    /// single-room hosted agent, unchanged. True (with <see cref="LiveAgentEnabled"/>) ⇒ the
    /// agent serves every room whose owner opted in, up to <see cref="MaxConcurrentRooms"/>.
    /// </summary>
    public bool DispatcherEnabled { get; set; }

    /// <summary>
    /// Hard cap on rooms occupied at once. Rooms are cheap (a socket and a task); the expensive
    /// thing is a TURN, which is bounded separately by <see cref="MaxConcurrentTurns"/>.
    /// </summary>
    public int MaxConcurrentRooms { get; set; } = 2;

    /// <summary>
    /// Fairness cap: most rooms ONE tenant may occupy. Defaults to the same value as
    /// <see cref="MaxConcurrentRooms"/> so a single-tenant deployment is not crippled; a
    /// multi-tenant deployment should set it to 1 so no tenant can take every slot. Clamped into
    /// [1, <see cref="MaxConcurrentRooms"/>].
    /// </summary>
    public int MaxRoomsPerTenant { get; set; } = 2;

    /// <summary>
    /// THE resource-safety knob. However many rooms are open, at most this many voice turns may
    /// contend for the process-wide chat/speech inference leases at once (this deployment runs
    /// <c>SemaphoreLimits:Chat = 1</c> and <c>Speech = 1</c>). Keep it at 1 unless those limits
    /// are raised too, otherwise voice simply queues in front of itself.
    /// </summary>
    public int MaxConcurrentTurns { get; set; } = 1;

    /// <summary>
    /// No inbound utterance for this long ⇒ leave the room and free the slot. This is the ONLY
    /// thing that reclaims a room whose caller walked away: while the agent is in a room the
    /// room is never empty, so LiveKit's own empty-room timeout cannot fire.
    /// </summary>
    public int RoomIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Wall-clock budget for one turn, INCLUDING the wait for the turn gate and the model
    /// leases inside it. Overrun cancels the turn, which releases every lease it holds.
    /// </summary>
    public int TurnBudgetSeconds { get; set; } = 60;

    /// <summary>How often the consent ledger is re-read and finished sessions are reaped.</summary>
    public int DispatcherPollSeconds { get; set; } = 2;

    /// <summary>
    /// How long a room waits before the agent may re-join it after a session ended. Bounds the
    /// retry rate of a room that cannot be joined at all (unreachable LiveKit, bad token) —
    /// without it such a room would be retried every poll for ever. Mirrors the single-room
    /// agent's 10 second reconnect delay.
    /// </summary>
    public int RoomRejoinDelaySeconds { get; set; } = 10;

    /// <summary>How long shutdown waits for cancelled room sessions before abandoning them.</summary>
    public int DispatcherShutdownDrainSeconds { get; set; } = 15;

    // ── Per-user consent (see VoiceAgentConsentRegistry) ──

    /// <summary>
    /// How long one <c>agent=true</c> token request keeps the agent welcome. Matches the 2 hour
    /// lifetime of the LiveKit token that request hands the browser, so consent never outlives
    /// the credential it was granted alongside.
    /// </summary>
    public int AgentConsentTtlMinutes { get; set; } = 120;

    /// <summary>Total consent grants tracked in memory; an authenticated caller cannot grow this without bound.</summary>
    public int MaxAgentConsentGrants { get; set; } = 256;

    /// <summary>Grants one user may hold at once (one per browser tab/label); the oldest is evicted beyond this.</summary>
    public int MaxAgentConsentGrantsPerUser { get; set; } = 4;

    /// <summary>True only when the dispatcher is actually meant to run (both switches on).</summary>
    public bool DispatcherActive => LiveAgentEnabled && DispatcherEnabled;

    /// <summary>
    /// Validates the dispatcher knobs. Returns false with a human-readable reason so the hosted
    /// service can stand down loudly instead of crashing startup or, worse, running with a
    /// nonsense cap. Values are checked, never silently corrected.
    /// </summary>
    public bool TryValidateDispatcher(out string? error)
    {
        error = null;
        if (MaxConcurrentRooms is < 1 or > 64)
        {
            error = "Voice:MaxConcurrentRooms must be between 1 and 64.";
            return false;
        }
        if (MaxRoomsPerTenant < 1 || MaxRoomsPerTenant > MaxConcurrentRooms)
        {
            error = "Voice:MaxRoomsPerTenant must be between 1 and Voice:MaxConcurrentRooms.";
            return false;
        }
        if (MaxConcurrentTurns is < 1 or > 16)
        {
            error = "Voice:MaxConcurrentTurns must be between 1 and 16.";
            return false;
        }
        if (RoomIdleTimeoutSeconds is < 5 or > 3600)
        {
            error = "Voice:RoomIdleTimeoutSeconds must be between 5 and 3600.";
            return false;
        }
        if (TurnBudgetSeconds is < 5 or > 600)
        {
            error = "Voice:TurnBudgetSeconds must be between 5 and 600.";
            return false;
        }
        if (DispatcherPollSeconds is < 1 or > 60)
        {
            error = "Voice:DispatcherPollSeconds must be between 1 and 60.";
            return false;
        }
        if (DispatcherShutdownDrainSeconds is < 1 or > 120)
        {
            error = "Voice:DispatcherShutdownDrainSeconds must be between 1 and 120.";
            return false;
        }
        if (RoomRejoinDelaySeconds is < 0 or > 600)
        {
            error = "Voice:RoomRejoinDelaySeconds must be between 0 and 600.";
            return false;
        }
        if (AgentConsentTtlMinutes is < 1 or > 1440)
        {
            error = "Voice:AgentConsentTtlMinutes must be between 1 and 1440.";
            return false;
        }
        if (MaxAgentConsentGrants is < 1 or > 10000)
        {
            error = "Voice:MaxAgentConsentGrants must be between 1 and 10000.";
            return false;
        }
        if (MaxAgentConsentGrantsPerUser < 1 || MaxAgentConsentGrantsPerUser > MaxAgentConsentGrants)
        {
            error = "Voice:MaxAgentConsentGrantsPerUser must be between 1 and Voice:MaxAgentConsentGrants.";
            return false;
        }
        return true;
    }

    /// <summary>Dispatcher-wide caps built from the validated options.</summary>
    public VoiceDispatcherLimits ToDispatcherLimits() => new()
    {
        MaxConcurrentRooms = MaxConcurrentRooms,
        MaxRoomsPerTenant = Math.Clamp(MaxRoomsPerTenant, 1, Math.Max(1, MaxConcurrentRooms)),
        PollInterval = TimeSpan.FromSeconds(DispatcherPollSeconds),
        RejoinDelay = TimeSpan.FromSeconds(RoomRejoinDelaySeconds),
        ShutdownDrainTimeout = TimeSpan.FromSeconds(DispatcherShutdownDrainSeconds)
    };

    /// <summary>Per-room bounds built from the validated options.</summary>
    public VoiceRoomSessionLimits ToSessionLimits() => new()
    {
        IdleTimeout = TimeSpan.FromSeconds(RoomIdleTimeoutSeconds),
        TurnBudget = TimeSpan.FromSeconds(TurnBudgetSeconds),
        PublishBudget = TimeSpan.FromSeconds(Math.Max(5, TurnBudgetSeconds / 2)),
        LeaveBudget = TimeSpan.FromSeconds(10),
        MaxConsecutiveTurnFailures = 3
    };

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
