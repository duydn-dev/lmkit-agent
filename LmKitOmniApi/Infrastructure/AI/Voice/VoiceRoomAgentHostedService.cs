using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Optional hosted service for the real-time voice room agent.
///
/// STRICT NO-OP by default: when <c>Voice:LiveAgentEnabled</c> is false (the default) it does
/// nothing at all. Even when enabled it stands down without joining a room, because the LiveKit
/// media join is a live-only stub (see <see cref="StubLiveKitMediaSession"/>) — it must never
/// crash application startup in CI or in a normal deployment.
///
/// <see cref="RunSessionAsync"/> is the reference turn loop that a real deployment would run once
/// live STT/LLM/TTS engines and a real <see cref="ILiveKitMediaSession"/> are wired. It is kept
/// <c>internal</c> so it is compiled and can be unit-tested with fakes, but the hosted service
/// itself never invokes it in this build.
/// </summary>
public sealed class VoiceRoomAgentHostedService : BackgroundService
{
    private readonly VoiceOptions _options;
    private readonly ILogger<VoiceRoomAgentHostedService> _logger;

    public VoiceRoomAgentHostedService(
        IOptions<VoiceOptions> options,
        ILogger<VoiceRoomAgentHostedService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.LiveAgentEnabled)
        {
            _logger.LogDebug(
                "Voice live room agent disabled (Voice:LiveAgentEnabled=false); hosted service is a no-op.");
            return Task.CompletedTask;
        }

        // Enabled but not wireable in this build: log clearly and stand down rather than
        // attempting a join that would throw and take down the host.
        _logger.LogWarning(
            "Voice live room agent is enabled but will not join any room: {Message}",
            StubLiveKitMediaSession.LiveOnlyMessage);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reference implementation of the live turn loop: join the room, then for each inbound
    /// utterance run one STT → LLM → TTS turn and publish the spoken reply. NOT invoked in this
    /// build; provided so the join/loop wiring is concrete and testable with fakes.
    /// </summary>
    internal static async Task RunSessionAsync(
        ILiveKitMediaSession session,
        IVoiceRoomAgent agent,
        VoiceRoomOptions room,
        CancellationToken ct)
    {
        await session.JoinAsync(room, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var utterance = await session.ReadUtteranceAsync(ct);
                if (utterance is null)
                    break; // room closed

                var result = await agent.RunTurnAsync(
                    new VoiceTurnContext { InboundAudio = utterance.Value, Voice = room.Voice },
                    ct);

                if (result.Handled && result.ReplyAudio.Length > 0)
                    await session.PublishAsync(result.ReplyAudio, ct);
            }
        }
        finally
        {
            await session.LeaveAsync(ct);
        }
    }
}
