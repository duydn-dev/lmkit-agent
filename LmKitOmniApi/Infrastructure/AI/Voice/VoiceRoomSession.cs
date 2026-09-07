using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>Why a dispatched room session stopped. Every value ends with the room left and the slot freed.</summary>
public enum VoiceRoomSessionEndReason
{
    /// <summary>The media session reported the room closed (caller hung up / track ended).</summary>
    RoomClosed,

    /// <summary>No utterance arrived within the idle timeout — the room is reclaimed.</summary>
    Idle,

    /// <summary>The dispatcher cancelled the session (shutdown, or consent lapsed/was revoked).</summary>
    Cancelled,

    /// <summary>Too many consecutive turn failures/timeouts; the room is dropped rather than spun on.</summary>
    TurnFailures,

    /// <summary>Joining the room failed.</summary>
    JoinFailed
}

/// <summary>
/// Bounds for ONE dispatched room session. These are the knobs that stop N rooms from starving
/// a process whose <c>SemaphoreLimits:Chat</c> and <c>SemaphoreLimits:Speech</c> are both 1.
/// </summary>
public sealed record VoiceRoomSessionLimits
{
    /// <summary>No inbound utterance for this long ⇒ leave the room and free the slot.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Wall-clock budget for ONE turn, INCLUDING the wait for the shared turn gate and the
    /// model inference leases inside it. A turn that overruns is cancelled, which releases every
    /// lease it holds (they are all <c>await using</c>), so a wedged model can never pin the
    /// process-wide chat/speech gate for the life of a room.
    /// </summary>
    public TimeSpan TurnBudget { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Budget for publishing one reply, so a stuck egress cannot hang the loop either.</summary>
    public TimeSpan PublishBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Budget for leaving the room during teardown; shutdown must not be able to hang.</summary>
    public TimeSpan LeaveBudget { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive failed/timed-out turns before the session gives the room up.</summary>
    public int MaxConsecutiveTurnFailures { get; init; } = 3;
}

/// <summary>
/// The turn loop for ONE dispatched room. Deliberately separate from
/// <c>VoiceRoomAgentHostedService.RunSessionAsync</c>, which is the shipped single-user loop and
/// stays exactly as it is: this one adds the bounds a multi-room dispatcher needs (idle timeout,
/// per-turn budget, a shared turn gate, failure back-off) and those bounds would be a behaviour
/// change for the single-room agent that has run without them.
///
/// LOCK ORDER — the one rule that keeps this deadlock-free:
/// <c>turn gate → model inference lease</c>, never the reverse. Nothing in the process acquires
/// the voice turn gate while holding a model lease, so the two can never form a cycle. Both waits
/// are cancellable and the turn gate is released in a <c>finally</c>, so a faulting, cancelled or
/// timed-out turn always gives it back.
/// </summary>
public static class VoiceRoomSession
{
    public static async Task<VoiceRoomSessionEndReason> RunAsync(
        ILiveKitMediaSession session,
        IVoiceRoomAgent agent,
        VoiceRoomOptions room,
        VoiceAgentGrant grant,
        VoiceRoomSessionLimits limits,
        SemaphoreSlim? turnGate,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await session.JoinAsync(room, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return VoiceRoomSessionEndReason.Cancelled;
        }
        catch (Exception ex)
        {
            // Nothing to leave: the join never completed. Report rather than throw so the
            // dispatcher frees the slot on the normal path.
            logger.LogWarning(ex, "🎙️ Voice room '{Room}' could not be joined.", room.Room);
            return VoiceRoomSessionEndReason.JoinFailed;
        }

        var reason = VoiceRoomSessionEndReason.Cancelled;
        try
        {
            reason = await PumpAsync(session, agent, room, grant, limits, turnGate, logger, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = VoiceRoomSessionEndReason.Cancelled;
        }
        finally
        {
            // Teardown must NOT use the session token: at shutdown it is already cancelled, and a
            // cancelled leave is how an agent participant is left behind in a live room. Bounded
            // so a wedged native teardown cannot hang host shutdown either.
            try
            {
                var left = await VoiceBudget
                    .RunAsync(token => session.LeaveAsync(token), limits.LeaveBudget, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!left)
                    logger.LogWarning(
                        "🎙️ Voice room '{Room}' did not finish leaving within {Seconds}s; continuing teardown.",
                        room.Room, limits.LeaveBudget.TotalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "🎙️ Voice room '{Room}' threw while leaving; ignoring during teardown.", room.Room);
            }
        }

        return reason;
    }

    private static async Task<VoiceRoomSessionEndReason> PumpAsync(
        ILiveKitMediaSession session,
        IVoiceRoomAgent agent,
        VoiceRoomOptions room,
        VoiceAgentGrant grant,
        VoiceRoomSessionLimits limits,
        SemaphoreSlim? turnGate,
        ILogger logger,
        CancellationToken ct)
    {
        var consecutiveFailures = 0;
        var failureCap = Math.Max(1, limits.MaxConsecutiveTurnFailures);

        while (!ct.IsCancellationRequested)
        {
            var (heard, utterance) = await VoiceBudget
                .RunAsync(token => session.ReadUtteranceAsync(token), limits.IdleTimeout, ct)
                .ConfigureAwait(false);

            if (!heard)
            {
                logger.LogInformation(
                    "🎙️ Voice room '{Room}' idle for {Seconds}s; leaving and releasing the slot.",
                    room.Room, limits.IdleTimeout.TotalSeconds);
                return VoiceRoomSessionEndReason.Idle;
            }
            if (utterance is null)
                return VoiceRoomSessionEndReason.RoomClosed;

            VoiceTurnResult? result = null;
            var failed = false;
            try
            {
                var (turned, turnResult) = await VoiceBudget
                    .RunAsync(
                        token => RunGatedTurnAsync(agent, turnGate, utterance.Value, room, grant, token),
                        limits.TurnBudget,
                        ct)
                    .ConfigureAwait(false);

                if (turned) result = turnResult;
                else
                {
                    failed = true;
                    logger.LogWarning(
                        "🎙️ Voice turn in room '{Room}' exceeded its {Seconds}s budget (model lease contention or a wedged inference); dropping the utterance.",
                        room.Room, limits.TurnBudget.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return VoiceRoomSessionEndReason.Cancelled;
            }
            catch (Exception ex)
            {
                failed = true;
                logger.LogWarning(ex, "🎙️ Voice turn in room '{Room}' failed; continuing.", room.Room);
            }

            if (!failed && result is { Handled: true, ReplyAudio.Length: > 0 })
            {
                var replyAudio = result.ReplyAudio;
                try
                {
                    var published = await VoiceBudget
                        .RunAsync(token => session.PublishAsync(replyAudio, token), limits.PublishBudget, ct)
                        .ConfigureAwait(false);
                    if (!published)
                    {
                        failed = true;
                        logger.LogWarning(
                            "🎙️ Publishing the reply in room '{Room}' exceeded its {Seconds}s budget.",
                            room.Room, limits.PublishBudget.TotalSeconds);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return VoiceRoomSessionEndReason.Cancelled;
                }
                catch (Exception ex)
                {
                    failed = true;
                    logger.LogWarning(ex, "🎙️ Publishing the reply in room '{Room}' failed; continuing.", room.Room);
                }
            }

            consecutiveFailures = failed ? consecutiveFailures + 1 : 0;
            if (consecutiveFailures >= failureCap)
            {
                logger.LogWarning(
                    "🎙️ Voice room '{Room}' had {Count} consecutive failed turns; dropping the room instead of spinning on it.",
                    room.Room, consecutiveFailures);
                return VoiceRoomSessionEndReason.TurnFailures;
            }
        }

        return VoiceRoomSessionEndReason.Cancelled;
    }

    /// <summary>
    /// One turn, taken under the process-wide voice turn gate. The gate — not the room count —
    /// is what bounds pressure on the single chat/speech inference leases: however many rooms are
    /// open, only <c>Voice:MaxConcurrentTurns</c> of them can be contending for a model at once.
    /// The gate is released in a <c>finally</c>, so a throwing, cancelled or timed-out turn
    /// always gives it back.
    /// </summary>
    private static async Task<VoiceTurnResult> RunGatedTurnAsync(
        IVoiceRoomAgent agent,
        SemaphoreSlim? turnGate,
        ReadOnlyMemory<byte> utterance,
        VoiceRoomOptions room,
        VoiceAgentGrant grant,
        CancellationToken ct)
    {
        var context = new VoiceTurnContext
        {
            InboundAudio = utterance,
            Voice = room.Voice,
            TenantId = grant.TenantId,
            UserId = grant.UserId,
            Room = grant.Room
        };

        if (turnGate is null)
            return await agent.RunTurnAsync(context, ct).ConfigureAwait(false);

        await turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await agent.RunTurnAsync(context, ct).ConfigureAwait(false);
        }
        finally
        {
            turnGate.Release();
        }
    }
}
