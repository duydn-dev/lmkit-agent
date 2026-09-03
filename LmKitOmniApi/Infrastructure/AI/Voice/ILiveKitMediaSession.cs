using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>Parameters for joining a LiveKit room as the voice agent participant.</summary>
public sealed class VoiceRoomOptions
{
    public string Url { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string Room { get; init; } = "omni-room";
    public string Identity { get; init; } = "voice-agent";
    public string Voice { get; init; } = "default";
}

/// <summary>
/// Real-time media transport for the voice room agent: the wiring point where the agent
/// joins a LiveKit room, receives inbound utterances, and publishes synthesized replies.
///
/// This is the LIVE-ONLY boundary. It cannot be exercised in CI because it needs a running
/// LiveKit server, a real audio track, and endpointing/VAD to segment utterances. The only
/// implementation shipped here is <see cref="StubLiveKitMediaSession"/>, which refuses to
/// join and documents what a real implementation must do.
/// </summary>
public interface ILiveKitMediaSession : IAsyncDisposable
{
    /// <summary>Connects to the room and begins receiving the caller's audio track.</summary>
    Task JoinAsync(VoiceRoomOptions options, CancellationToken ct = default);

    /// <summary>
    /// Awaits the next complete inbound utterance (post-endpointing) as WAV/PCM bytes,
    /// or null when the room has closed and no more audio will arrive.
    /// </summary>
    Task<ReadOnlyMemory<byte>?> ReadUtteranceAsync(CancellationToken ct = default);

    /// <summary>Publishes synthesized reply audio (WAV bytes) back to the room.</summary>
    Task PublishAsync(ReadOnlyMemory<byte> wavAudio, CancellationToken ct = default);

    /// <summary>Leaves the room and releases the media tracks.</summary>
    Task LeaveAsync(CancellationToken ct = default);
}

/// <summary>
/// Live-only placeholder for <see cref="ILiveKitMediaSession"/>. Every media operation throws
/// <see cref="NotSupportedException"/> because real-time LiveKit audio ingest/egress is not
/// wired in this build. This is deliberate: it is a clearly-labelled stub, not a fake that
/// pretends to move audio.
/// </summary>
public sealed class StubLiveKitMediaSession : ILiveKitMediaSession
{
    public const string LiveOnlyMessage =
        "LiveKit media join is a live-only stub: real-time audio ingest/egress is not wired in this build and cannot run in CI.";

    private readonly ILogger<StubLiveKitMediaSession> _logger;

    public StubLiveKitMediaSession(ILogger<StubLiveKitMediaSession> logger) => _logger = logger;

    public Task JoinAsync(VoiceRoomOptions options, CancellationToken ct = default)
    {
        _logger.LogWarning("Refusing to join LiveKit room {Room}: {Message}", options.Room, LiveOnlyMessage);
        throw new NotSupportedException(LiveOnlyMessage);
    }

    public Task<ReadOnlyMemory<byte>?> ReadUtteranceAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(LiveOnlyMessage);

    public Task PublishAsync(ReadOnlyMemory<byte> wavAudio, CancellationToken ct = default) =>
        throw new NotSupportedException(LiveOnlyMessage);

    public Task LeaveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
