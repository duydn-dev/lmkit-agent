using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Runs ONE consented room to completion. The production implementation joins LiveKit; tests
/// substitute a fake, which is the whole reason the dispatcher's bounds are CI-verifiable
/// without a LiveKit server.
/// </summary>
public interface IVoiceRoomSessionRunner
{
    /// <summary>
    /// Joins <paramref name="grant"/>'s room, serves it, and returns when the room ends. Must
    /// honour <paramref name="ct"/> and must never throw for an ordinary end-of-session.
    /// </summary>
    Task RunAsync(VoiceAgentGrant grant, CancellationToken ct);
}

/// <summary>Hard bounds for the whole dispatcher. Every one of these is a cap, never a target.</summary>
public sealed record VoiceDispatcherLimits
{
    /// <summary>Most rooms occupied at once across the process.</summary>
    public int MaxConcurrentRooms { get; init; } = 2;

    /// <summary>Most rooms one TENANT may occupy — the fairness knob. Never above <see cref="MaxConcurrentRooms"/>.</summary>
    public int MaxRoomsPerTenant { get; init; } = 2;

    /// <summary>How often the consent ledger is re-read and finished sessions are reaped.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a room waits before it may be dispatched again after a session for it ended.
    /// Without this a room that fails to join — an unreachable LiveKit, a bad token — would be
    /// retried every <see cref="PollInterval"/> for ever, minting tokens and building DI scopes
    /// in a hot loop. Mirrors the single-room agent's reconnect delay.
    /// </summary>
    public TimeSpan RejoinDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long shutdown waits for cancelled sessions before giving up on them.</summary>
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The bounded multi-room voice dispatcher.
///
/// WHAT IT DOES: reads the consent ledger (<see cref="IVoiceAgentConsentRegistry"/>), owns at
/// most one session per room, starts sessions for rooms whose owner asked for an agent, and
/// cancels sessions whose consent lapsed or was withdrawn.
///
/// WHAT IT DELIBERATELY DOES NOT DO: discover rooms from LiveKit. Neither
/// <c>room_started</c>/<c>participant_joined</c> webhooks (which need a publicly reachable
/// endpoint this deployment does not have, and which cannot be exercised without a LiveKit
/// server) nor polling <c>RoomServiceClient.ListRooms</c> would answer the question that
/// actually matters — did this user ASK for an agent — and both would put an agent participant
/// into calls of users who never consented. The token endpoint already knows the answer, so
/// consent IS the room list.
///
/// RESOURCE SAFETY — the properties the tests hold:
/// <list type="bullet">
///   <item>Room slots are taken with a NON-BLOCKING try. The dispatcher loop never waits for a
///   slot, so it can never deadlock against the sessions it owns.</item>
///   <item>A slot is freed on EVERY exit path — normal end, fault, or cancellation — because the
///   session task swallows and logs, and the reap pass keys off task completion, not outcome.</item>
///   <item>Rooms are cheap; TURNS are expensive. A separate <see cref="SemaphoreSlim"/> turn gate
///   (<c>Voice:MaxConcurrentTurns</c>) bounds how many rooms may contend for the process-wide
///   chat/speech inference leases at once, so raising the room cap cannot starve HTTP traffic.</item>
///   <item>Per-tenant caps mean one tenant cannot take every slot.</item>
///   <item>Shutdown cancels every session and drains with a timeout, so it cannot hang.</item>
/// </list>
/// </summary>
public sealed class VoiceRoomDispatcher
{
    private readonly IVoiceAgentConsentRegistry _registry;
    private readonly IVoiceRoomSessionRunner _runner;
    private readonly VoiceDispatcherLimits _limits;
    private readonly ILogger _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, RoomSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Room ⇒ earliest moment it may be dispatched again. Bounds the retry rate of a failing room.</summary>
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);

    public VoiceRoomDispatcher(
        IVoiceAgentConsentRegistry registry,
        IVoiceRoomSessionRunner runner,
        VoiceDispatcherLimits limits,
        ILogger logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Rooms currently occupied. Test/diagnostic surface.</summary>
    public int ActiveRoomCount
    {
        get { lock (_lock) return _sessions.Count; }
    }

    /// <summary>Room names currently occupied, sorted. Test/diagnostic surface.</summary>
    public IReadOnlyList<string> ActiveRooms
    {
        get { lock (_lock) return _sessions.Keys.OrderBy(room => room, StringComparer.Ordinal).ToList(); }
    }

    /// <summary>
    /// The dispatcher loop: tick, sleep, repeat, then drain. Returns only on cancellation, and
    /// always after every session it started has been cancelled and awaited (or the drain timed
    /// out and said so).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "🎙️ Voice room dispatcher started (max {Rooms} rooms, {PerTenant} per tenant, poll {Poll}s).",
            _limits.MaxConcurrentRooms, _limits.MaxRoomsPerTenant, _limits.PollInterval.TotalSeconds);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Tick(DateTimeOffset.UtcNow, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A bad tick must never end the dispatcher: the next one re-reads consent.
                    _logger.LogError(ex, "🎙️ Voice room dispatcher tick failed; continuing.");
                }

                try
                {
                    await Task.Delay(_limits.PollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await DrainAsync().ConfigureAwait(false);
            _logger.LogInformation("🎙️ Voice room dispatcher stopped.");
        }
    }

    /// <summary>
    /// One pass: reap finished sessions, cancel sessions whose consent is gone, then fill free
    /// slots from the consent ledger in fair order. Internal so the caps and fairness can be
    /// driven deterministically from tests rather than raced against a timer.
    /// </summary>
    internal void Tick(DateTimeOffset nowUtc, CancellationToken ct)
    {
        Reap(nowUtc);

        var grants = _registry.ActiveGrants(nowUtc);
        var wanted = new HashSet<string>(grants.Select(grant => grant.Room), StringComparer.Ordinal);

        // Consent withdrawn or lapsed ⇒ the agent leaves. This is the half of "opt-in" that
        // people forget: an opt-in you cannot take back is not consent.
        List<RoomSession> evicted;
        lock (_lock)
        {
            evicted = _sessions.Values.Where(session => !wanted.Contains(session.Room)).ToList();
        }
        foreach (var session in evicted)
        {
            _logger.LogInformation("🎙️ Voice consent for room '{Room}' ended; cancelling its session.", session.Room);
            session.Cancel();
        }

        if (ct.IsCancellationRequested) return;

        foreach (var grant in OrderForFairness(grants))
        {
            if (ct.IsCancellationRequested) return;

            // Defence in depth: never join a room whose name does not belong to the identity on
            // the grant. The registry derives the name, so this can only fire on a bug — which
            // is exactly the bug that would put an agent into another tenant's call.
            if (!grant.IsSelfConsistent())
            {
                _logger.LogError(
                    "🎙️ Refusing voice room '{Room}': its name does not match tenant {TenantId}/user {UserId} on the grant.",
                    grant.Room, grant.TenantId, grant.UserId);
                continue;
            }

            if (!TryStart(grant, nowUtc, ct)) continue;
        }
    }

    /// <summary>
    /// Fairness: tenants with fewer live rooms are offered slots first, then oldest consent
    /// first, then room name — a total order, so the outcome under contention is deterministic
    /// rather than "whichever grant the dictionary happened to yield first".
    /// </summary>
    private IEnumerable<VoiceAgentGrant> OrderForFairness(IReadOnlyList<VoiceAgentGrant> grants)
    {
        Dictionary<Guid, int> perTenant;
        lock (_lock)
        {
            perTenant = _sessions.Values
                .GroupBy(session => session.TenantId)
                .ToDictionary(group => group.Key, group => group.Count());
        }

        return grants
            .OrderBy(grant => perTenant.TryGetValue(grant.TenantId, out var count) ? count : 0)
            .ThenBy(grant => grant.GrantedAtUtc)
            .ThenBy(grant => grant.Room, StringComparer.Ordinal);
    }

    /// <summary>
    /// Takes a slot without ever blocking and starts the session, or returns false. The whole
    /// admission decision happens under one lock so two ticks (or a tick and a reap) cannot
    /// both believe the last slot is free.
    /// </summary>
    private bool TryStart(VoiceAgentGrant grant, DateTimeOffset nowUtc, CancellationToken ct)
    {
        RoomSession session;
        lock (_lock)
        {
            if (_sessions.ContainsKey(grant.Room)) return false;

            // A room whose last session only just ended waits before being re-dispatched, so a
            // room that cannot be joined at all retries on a delay instead of in a hot loop.
            if (_cooldown.TryGetValue(grant.Room, out var notBefore))
            {
                if (nowUtc < notBefore) return false;
                _cooldown.Remove(grant.Room);
            }

            var roomCap = Math.Max(1, _limits.MaxConcurrentRooms);
            if (_sessions.Count >= roomCap) return false;

            var tenantCap = Math.Clamp(_limits.MaxRoomsPerTenant, 1, roomCap);
            var tenantRooms = _sessions.Values.Count(existing => existing.TenantId == grant.TenantId);
            if (tenantRooms >= tenantCap) return false;

            session = new RoomSession(grant, ct);
            _sessions[grant.Room] = session;
        }

        // Started OUTSIDE the lock and with CancellationToken.None so the task always exists and
        // always completes — a task that never starts is a slot that is never reclaimed.
        //
        // The body waits on `admitted` so the task is recorded on the slot BEFORE it can run:
        // otherwise a session that ends immediately (a join that fails outright) could complete
        // while Task is still null, and a slot whose Task is null is never reaped.
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(async () =>
        {
            await admitted.Task.ConfigureAwait(false);
            try
            {
                await _runner.RunAsync(grant, session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
            {
                // Normal shutdown / consent withdrawal.
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: the slot is reclaimed by completion, not by outcome, and
                // one bad room must not take the dispatcher (or the host) down with it.
                _logger.LogError(ex, "🎙️ Voice room session for '{Room}' failed.", grant.Room);
            }
        }, CancellationToken.None);

        lock (_lock) session.Task = running;
        admitted.SetResult();

        _logger.LogInformation(
            "🎙️ Voice agent joining room '{Room}' for tenant {TenantId} (rooms now {Count}/{Cap}).",
            grant.Room, grant.TenantId, ActiveRoomCount, _limits.MaxConcurrentRooms);
        return true;
    }

    /// <summary>Removes finished sessions, freeing their slot. Faulted or cancelled counts as finished.</summary>
    private void Reap(DateTimeOffset nowUtc)
    {
        List<RoomSession> finished;
        lock (_lock)
        {
            finished = _sessions.Values.Where(session => session.IsFinished).ToList();
            foreach (var session in finished)
            {
                _sessions.Remove(session.Room);
                _cooldown[session.Room] = nowUtc + _limits.RejoinDelay;
            }

            // Keep the cooldown map from growing: an entry is only useful until it lapses.
            if (_cooldown.Count > 0)
            {
                var lapsed = _cooldown.Where(entry => entry.Value <= nowUtc).Select(entry => entry.Key).ToList();
                foreach (var room in lapsed) _cooldown.Remove(room);
            }
        }
        foreach (var session in finished)
        {
            session.Dispose();
            _logger.LogInformation("🎙️ Voice room '{Room}' released; slot free.", session.Room);
        }
    }

    /// <summary>
    /// Cancels every session and waits, bounded. Never throws: the session tasks already swallow
    /// their own faults, and a drain that times out is logged rather than propagated so host
    /// shutdown continues.
    /// </summary>
    private async Task DrainAsync()
    {
        List<RoomSession> sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }
        if (sessions.Count == 0) return;

        foreach (var session in sessions) session.Cancel();

        var running = sessions.Select(session => session.Task ?? Task.CompletedTask).ToArray();
        var all = Task.WhenAll(running);
        var first = await Task.WhenAny(all, Task.Delay(_limits.ShutdownDrainTimeout)).ConfigureAwait(false);

        if (ReferenceEquals(first, all))
        {
            // The session tasks swallow their own faults, so this only observes completion.
            try { await all.ConfigureAwait(false); } catch { /* already logged per session */ }
        }
        else
        {
            _logger.LogWarning(
                "🎙️ {Count} voice room session(s) did not stop within {Seconds}s; abandoning them so shutdown can finish.",
                running.Count(task => !task.IsCompleted), _limits.ShutdownDrainTimeout.TotalSeconds);
        }

        foreach (var session in sessions)
        {
            if (session.IsFinished) session.Dispose();
        }
    }

    /// <summary>One occupied room slot: its grant, its cancellation, and the task serving it.</summary>
    private sealed class RoomSession : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public RoomSession(VoiceAgentGrant grant, CancellationToken parent)
        {
            Grant = grant;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public VoiceAgentGrant Grant { get; }
        public string Room => Grant.Room;
        public Guid TenantId => Grant.TenantId;
        public CancellationToken Token => _cts.Token;
        public Task? Task { get; set; }

        /// <summary>A slot whose task has not been assigned yet is NOT finished — it is mid-start.</summary>
        public bool IsFinished => Task is { IsCompleted: true };

        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
        }

        /// <summary>Only ever called once the task has completed, so nothing still holds the token.</summary>
        public void Dispose()
        {
            try { _cts.Dispose(); } catch (ObjectDisposedException) { /* idempotent */ }
        }
    }
}
