using System.Collections.Concurrent;
using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The multi-room dispatcher's SAFETY properties, not its happy path.
///
/// A dispatcher that can deadlock or starve the process is worse than no dispatcher: this
/// deployment runs <c>SemaphoreLimits:Chat = 1</c> and <c>Speech = 1</c>, so every live room is
/// a potential competitor for the only model lease there is. These tests hold the caps under
/// contention, prove a slot is returned on every exit path (normal, faulted, cancelled), prove
/// consent can be withdrawn, prove shutdown cannot hang, and prove two tenants' sessions never
/// see each other's context.
///
/// The LiveKit boundary is faked exactly where the production code puts it —
/// <see cref="IVoiceRoomSessionRunner"/> — so all of this runs with no LiveKit server.
/// </summary>
public sealed class VoiceRoomDispatcherTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Longest any of these tests will wait for a background condition before failing.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ── caps ──

    [Fact]
    public async Task RoomCap_IsNeverExceeded_HoweverManyRoomsConsent()
    {
        var registry = new FakeRegistry(Grants(TenantA, 6));
        var runner = new FakeSessionRunner();                    // sessions block until cancelled
        var dispatcher = Dispatcher(registry, runner, rooms: 2, perTenant: 2);

        for (var i = 0; i < 5; i++) dispatcher.Tick(Now, CancellationToken.None);

        Assert.Equal(2, dispatcher.ActiveRoomCount);
        await WaitForAsync(() => runner.TotalStarted == 2, "both admitted rooms to start");
        Assert.Equal(2, runner.TotalStarted);

        await dispatcher.ShutdownAsync();
    }

    [Fact]
    public async Task RoomCap_HoldsUnderConcurrentDispatch()
    {
        var registry = new FakeRegistry(Grants(TenantA, 32));
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 3, perTenant: 3);

        // Sixteen threads racing to fill three slots. If admission were not decided under one
        // lock this is where the cap would be overshot.
        var overshoot = 0;
        await Parallel.ForAsync(0, 16, async (_, _) =>
        {
            for (var i = 0; i < 20; i++)
            {
                dispatcher.Tick(Now, CancellationToken.None);
                if (dispatcher.ActiveRoomCount > 3) Interlocked.Exchange(ref overshoot, 1);
                await Task.Yield();
            }
        });

        Assert.Equal(0, overshoot);
        Assert.Equal(3, dispatcher.ActiveRoomCount);
        await WaitForAsync(() => runner.TotalStarted == 3, "the three admitted rooms to start");
        Assert.Equal(3, runner.TotalStarted);
        Assert.Equal(3, runner.StartedRooms.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, runner.PeakConcurrentRooms);   // never more rooms live than the cap

        await dispatcher.ShutdownAsync();
    }

    [Fact]
    public async Task PerTenantCap_StopsOneTenantTakingEverySlot()
    {
        var grants = Grants(TenantA, 4).Concat(Grants(TenantB, 1)).ToList();
        var registry = new FakeRegistry(grants);
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 3, perTenant: 1);

        for (var i = 0; i < 5; i++) dispatcher.Tick(Now, CancellationToken.None);

        Assert.Equal(2, dispatcher.ActiveRoomCount);
        await WaitForAsync(() => runner.TotalStarted == 2, "the two admitted rooms to start");
        var tenants = runner.StartedGrants.Select(grant => grant.TenantId).ToList();
        Assert.Equal(1, tenants.Count(tenantId => tenantId == TenantA));
        Assert.Equal(1, tenants.Count(tenantId => tenantId == TenantB));

        await dispatcher.ShutdownAsync();
    }

    [Fact]
    public async Task Fairness_OffersFreeSlotsToTheTenantWithFewestRooms()
    {
        // Tenant A consented first and to more rooms; with a per-tenant cap of 2 and 3 slots,
        // B must still get one rather than being permanently queued behind A.
        var grants = Grants(TenantA, 3, startAt: Now).Concat(Grants(TenantB, 1, startAt: Now.AddSeconds(30))).ToList();
        var registry = new FakeRegistry(grants);
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 3, perTenant: 2);

        for (var i = 0; i < 5; i++) dispatcher.Tick(Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(3, dispatcher.ActiveRoomCount);
        await WaitForAsync(() => runner.TotalStarted == 3, "the three admitted rooms to start");
        Assert.Equal(2, runner.StartedGrants.Count(grant => grant.TenantId == TenantA));
        Assert.Equal(1, runner.StartedGrants.Count(grant => grant.TenantId == TenantB));

        await dispatcher.ShutdownAsync();
    }

    [Fact]
    public async Task OneRoom_NeverGetsTwoSessions()
    {
        var registry = new FakeRegistry(Grants(TenantA, 1));
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 4, perTenant: 4);

        for (var i = 0; i < 10; i++) dispatcher.Tick(Now, CancellationToken.None);

        Assert.Equal(1, dispatcher.ActiveRoomCount);
        await WaitForAsync(() => runner.TotalStarted == 1, "the single room to start");
        Assert.Equal(1, runner.TotalStarted);
        Assert.Equal(1, runner.PeakConcurrentRooms);

        await dispatcher.ShutdownAsync();
    }

    // ── slot reclamation on every exit path ──

    [Fact]
    public async Task AFaultingSession_ReleasesItsSlot_AndTheNextRoomIsServed()
    {
        var grants = Grants(TenantA, 2);
        var registry = new FakeRegistry(grants);
        var runner = new FakeSessionRunner
        {
            // The first room to be dispatched blows up; every other room blocks normally.
            Behaviour = (grant, token) => grant.Room == grants[0].Room
                ? Task.FromException(new InvalidOperationException("LiveKit exploded"))
                : Task.Delay(Timeout.Infinite, token)
        };
        var dispatcher = Dispatcher(registry, runner, rooms: 1, perTenant: 1, rejoin: TimeSpan.FromSeconds(10));

        dispatcher.Tick(Now, CancellationToken.None);
        Assert.Equal(1, dispatcher.ActiveRoomCount);

        await WaitForAsync(() => runner.Finished.Contains(grants[0].Room), "the faulting session to unwind");

        // The reap pass keys off completion, not outcome: the slot comes back and the queued
        // room is served. The faulting room is in its rejoin cooldown, so it cannot immediately
        // re-take the slot it just crashed out of.
        dispatcher.Tick(Now, CancellationToken.None);
        Assert.Equal(1, dispatcher.ActiveRoomCount);
        Assert.Equal(grants[1].Room, Assert.Single(dispatcher.ActiveRooms));

        await dispatcher.ShutdownAsync();
    }

    [Fact]
    public async Task ARoomThatCannotBeJoined_IsRetriedOnADelay_NotInAHotLoop()
    {
        var grants = Grants(TenantA, 1);
        var registry = new FakeRegistry(grants);
        var runner = new FakeSessionRunner
        {
            Behaviour = (_, _) => Task.FromException(new InvalidOperationException("LiveKit unreachable"))
        };
        var dispatcher = Dispatcher(registry, runner, rooms: 1, perTenant: 1, rejoin: TimeSpan.FromSeconds(10));

        dispatcher.Tick(Now, CancellationToken.None);
        await WaitForAsync(() => runner.Finished.Contains(grants[0].Room), "the failed join to unwind");

        dispatcher.Tick(Now.AddSeconds(1), CancellationToken.None);   // reaps ⇒ cooldown until Now+11s
        Assert.Equal(0, dispatcher.ActiveRoomCount);

        // Twenty ticks inside the cooldown window must not produce a single extra attempt.
        for (var i = 0; i < 20; i++) dispatcher.Tick(Now.AddSeconds(2), CancellationToken.None);
        Assert.Equal(1, runner.TotalStarted);

        // Once the cooldown lapses the room is retried — once.
        dispatcher.Tick(Now.AddSeconds(11), CancellationToken.None);
        await WaitForAsync(() => runner.TotalStarted == 2, "the delayed retry");
        for (var i = 0; i < 20; i++) dispatcher.Tick(Now.AddSeconds(12), CancellationToken.None);
        Assert.Equal(2, runner.TotalStarted);

        await dispatcher.ShutdownAsync();
    }

    [Fact]
    public async Task ASessionThatEndsNormally_ReleasesItsSlot()
    {
        var grants = Grants(TenantA, 2);
        var registry = new FakeRegistry(grants);
        var runner = new FakeSessionRunner
        {
            Behaviour = (grant, token) => grant.Room == grants[0].Room
                ? Task.CompletedTask                                   // caller hung up
                : Task.Delay(Timeout.Infinite, token)
        };
        var dispatcher = Dispatcher(registry, runner, rooms: 1, perTenant: 1, rejoin: TimeSpan.FromSeconds(10));

        dispatcher.Tick(Now, CancellationToken.None);
        await WaitForAsync(() => runner.Finished.Contains(grants[0].Room), "the first session to end");

        dispatcher.Tick(Now, CancellationToken.None);
        Assert.Equal(grants[1].Room, Assert.Single(dispatcher.ActiveRooms));

        await dispatcher.ShutdownAsync();
    }

    // ── consent is revocable, and lapses ──

    [Fact]
    public async Task WithdrawnConsent_CancelsTheSession_AndFreesTheSlot()
    {
        var grants = Grants(TenantA, 1);
        var registry = new FakeRegistry(grants);
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 2, perTenant: 2);

        dispatcher.Tick(Now, CancellationToken.None);
        Assert.Equal(1, dispatcher.ActiveRoomCount);

        registry.Set(Array.Empty<VoiceAgentGrant>());   // the user hung up / revoked
        dispatcher.Tick(Now, CancellationToken.None);   // notices and cancels

        await WaitForAsync(() => runner.Cancelled.Contains(grants[0].Room), "the session to observe cancellation");

        dispatcher.Tick(Now, CancellationToken.None);   // reaps
        Assert.Equal(0, dispatcher.ActiveRoomCount);
    }

    [Fact]
    public async Task LapsedConsent_CancelsTheSession()
    {
        // The fake registry hides expired grants exactly as the real one does.
        var grant = Grants(TenantA, 1)[0] with { ExpiresAtUtc = Now.AddMinutes(5) };
        var registry = new FakeRegistry(new[] { grant });
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 2, perTenant: 2);

        dispatcher.Tick(Now, CancellationToken.None);
        Assert.Equal(1, dispatcher.ActiveRoomCount);

        dispatcher.Tick(Now.AddMinutes(6), CancellationToken.None);   // grant has lapsed
        await WaitForAsync(() => runner.Cancelled.Contains(grant.Room), "the lapsed session to be cancelled");

        dispatcher.Tick(Now.AddMinutes(6), CancellationToken.None);
        Assert.Equal(0, dispatcher.ActiveRoomCount);
    }

    // ── multi-tenancy ──

    [Fact]
    public async Task TwoTenantsSessions_NeverSeeEachOthersContext()
    {
        var grants = Grants(TenantA, 2).Concat(Grants(TenantB, 2)).ToList();
        var registry = new FakeRegistry(grants);
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 4, perTenant: 2);

        for (var i = 0; i < 3; i++) dispatcher.Tick(Now, CancellationToken.None);
        Assert.Equal(4, dispatcher.ActiveRoomCount);
        await WaitForAsync(() => runner.TotalStarted == 4, "all four rooms to start");

        // Every session was handed exactly one grant, and the room it was asked to join parses
        // back to that grant's tenant and user. Nothing crosses.
        Assert.Equal(4, runner.StartedGrants.Count);
        foreach (var started in runner.StartedGrants)
        {
            Assert.True(VoiceRoomNaming.TryParseScopedRoom(started.Room, out var tenantId, out var userId, out _));
            Assert.Equal(started.TenantId, tenantId);
            Assert.Equal(started.UserId, userId);
        }
        Assert.Equal(2, runner.StartedGrants.Count(grant => grant.TenantId == TenantA));
        Assert.Equal(2, runner.StartedGrants.Count(grant => grant.TenantId == TenantB));
        Assert.Equal(4, runner.StartedGrants.Select(grant => grant.UserId).Distinct().Count());

        await dispatcher.ShutdownAsync();
    }

    [Fact]
    public void AGrantNamingSomeoneElsesRoom_IsNeverJoined()
    {
        // The registry derives room names, so this can only come from a bug — which is exactly
        // the bug that would put an agent into another tenant's call.
        Assert.True(VoiceRoomNaming.TryScopedRoom(TenantB, Guid.Parse("22222222-0000-0000-0000-000000000001"),
            "omni-room", out var tenantBsRoom, out _));
        var forged = new VoiceAgentGrant
        {
            Room = tenantBsRoom,
            TenantId = TenantA,
            UserId = Guid.Parse("11111111-0000-0000-0000-000000000001"),
            Label = "omni-room",
            GrantedAtUtc = Now,
            ExpiresAtUtc = Now.AddMinutes(30)
        };
        var registry = new FakeRegistry(new[] { forged });
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 4, perTenant: 4);

        dispatcher.Tick(Now, CancellationToken.None);

        Assert.Equal(0, dispatcher.ActiveRoomCount);
        Assert.Equal(0, runner.TotalStarted);
    }

    // ── shutdown ──

    [Fact]
    public async Task Shutdown_CancelsEverySession_AndReturns()
    {
        var registry = new FakeRegistry(Grants(TenantA, 3));
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 3, perTenant: 3, poll: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(cts.Token);
        await WaitForAsync(() => dispatcher.ActiveRoomCount == 3, "the dispatcher to fill its slots");

        cts.Cancel();
        var finished = await Task.WhenAny(loop, Task.Delay(Patience));
        Assert.Same(loop, finished);
        await loop;

        Assert.Equal(0, dispatcher.ActiveRoomCount);
        Assert.Equal(3, runner.Cancelled.Count);
    }

    [Fact]
    public async Task Shutdown_WithASessionThatIgnoresCancellation_StillReturns()
    {
        // A wedged native call is the realistic version of this: teardown must not wait on it
        // for ever, it must give up after the drain timeout and let the host finish stopping.
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var registry = new FakeRegistry(Grants(TenantA, 1));
            var runner = new FakeSessionRunner { Behaviour = (_, _) => wedged.Task };
            var dispatcher = Dispatcher(
                registry, runner, rooms: 1, perTenant: 1,
                poll: TimeSpan.FromMilliseconds(10), drain: TimeSpan.FromMilliseconds(200));

            using var cts = new CancellationTokenSource();
            var loop = dispatcher.RunAsync(cts.Token);
            await WaitForAsync(() => dispatcher.ActiveRoomCount == 1, "the wedged room to start");

            cts.Cancel();
            var finished = await Task.WhenAny(loop, Task.Delay(Patience));
            Assert.Same(loop, finished);
            await loop;
        }
        finally
        {
            wedged.TrySetResult();   // never leave the abandoned session running past the test
        }
    }

    [Fact]
    public async Task TheLoop_KeepsRunningWhenTheConsentLedgerThrows()
    {
        var grants = Grants(TenantA, 1);
        var registry = new FakeRegistry(grants) { ThrowOnce = true };
        var runner = new FakeSessionRunner();
        var dispatcher = Dispatcher(registry, runner, rooms: 1, perTenant: 1, poll: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(cts.Token);

        await WaitForAsync(() => dispatcher.ActiveRoomCount == 1, "the dispatcher to recover from a bad tick");

        cts.Cancel();
        await loop;
    }

    // ── helpers ──

    private static VoiceRoomDispatcher Dispatcher(
        IVoiceAgentConsentRegistry registry,
        IVoiceRoomSessionRunner runner,
        int rooms,
        int perTenant,
        TimeSpan? poll = null,
        TimeSpan? drain = null,
        TimeSpan? rejoin = null) =>
        new(registry, runner, new VoiceDispatcherLimits
        {
            MaxConcurrentRooms = rooms,
            MaxRoomsPerTenant = perTenant,
            PollInterval = poll ?? TimeSpan.FromMilliseconds(20),
            RejoinDelay = rejoin ?? TimeSpan.Zero,
            ShutdownDrainTimeout = drain ?? TimeSpan.FromSeconds(5)
        }, NullLogger.Instance);

    /// <summary>N self-consistent grants for one tenant, each owned by a DIFFERENT user.</summary>
    private static List<VoiceAgentGrant> Grants(Guid tenantId, int count, DateTimeOffset? startAt = null)
    {
        var start = startAt ?? Now;
        var grants = new List<VoiceAgentGrant>();
        for (var i = 0; i < count; i++)
        {
            var userId = Guid.Parse($"{tenantId:N}"[..8] + $"-0000-0000-0000-{i:D12}");
            Assert.True(VoiceRoomNaming.TryScopedRoom(tenantId, userId, "omni-room", out var room, out _));
            grants.Add(new VoiceAgentGrant
            {
                Room = room,
                TenantId = tenantId,
                UserId = userId,
                Label = "omni-room",
                GrantedAtUtc = start.AddSeconds(i),
                ExpiresAtUtc = start.AddMinutes(60)
            });
        }
        return grants;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out after {Patience.TotalSeconds}s waiting for {what}.");
    }

    /// <summary>A consent ledger under the test's control; mirrors the real one's expiry filter.</summary>
    private sealed class FakeRegistry : IVoiceAgentConsentRegistry
    {
        private readonly object _lock = new();
        private List<VoiceAgentGrant> _grants;
        private int _thrown;

        public FakeRegistry(IEnumerable<VoiceAgentGrant> grants) => _grants = grants.ToList();

        /// <summary>Makes the FIRST read fail, to prove a bad tick does not end the dispatcher.</summary>
        public bool ThrowOnce { get; init; }

        public int Count { get { lock (_lock) return _grants.Count; } }

        public void Set(IEnumerable<VoiceAgentGrant> grants)
        {
            lock (_lock) _grants = grants.ToList();
        }

        public IReadOnlyList<VoiceAgentGrant> ActiveGrants(DateTimeOffset nowUtc)
        {
            if (ThrowOnce && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new InvalidOperationException("consent ledger unavailable");
            lock (_lock)
                return _grants.Where(grant => grant.IsActiveAt(nowUtc)).ToList();
        }

        public bool Revoke(Guid tenantId, Guid userId, string? roomLabel) => false;

        public bool TryGrant(
            Guid tenantId, Guid userId, string? roomLabel, string? voice, DateTimeOffset nowUtc,
            out VoiceAgentGrant? grant, out string? error)
        {
            grant = null;
            error = "not supported in this fake";
            return false;
        }
    }

    /// <summary>
    /// Stands in for the LiveKit session runner. Records what it was asked to serve, how many
    /// rooms it ran at once, and which sessions observed cancellation.
    /// </summary>
    private sealed class FakeSessionRunner : IVoiceRoomSessionRunner
    {
        private readonly ConcurrentBag<VoiceAgentGrant> _started = new();
        private readonly ConcurrentDictionary<string, byte> _finished = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _cancelled = new(StringComparer.Ordinal);
        private int _running;
        private int _peak;

        /// <summary>Default: block until cancelled — a room that is being served.</summary>
        public Func<VoiceAgentGrant, CancellationToken, Task>? Behaviour { get; init; }

        public IReadOnlyCollection<VoiceAgentGrant> StartedGrants => _started.ToList();
        public IReadOnlyCollection<string> StartedRooms => _started.Select(grant => grant.Room).ToList();
        public int TotalStarted => _started.Count;
        public ICollection<string> Finished => _finished.Keys;
        public ICollection<string> Cancelled => _cancelled.Keys;
        public int PeakConcurrentRooms => Volatile.Read(ref _peak);

        public async Task RunAsync(VoiceAgentGrant grant, CancellationToken ct)
        {
            _started.Add(grant);
            var running = Interlocked.Increment(ref _running);
            int peak;
            while (running > (peak = Volatile.Read(ref _peak))
                   && Interlocked.CompareExchange(ref _peak, running, peak) != peak)
            {
                // retry
            }

            try
            {
                var behaviour = Behaviour ?? ((_, token) => Task.Delay(Timeout.Infinite, token));
                await behaviour(grant, ct);
            }
            catch (OperationCanceledException)
            {
                _cancelled[grant.Room] = 1;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                _finished[grant.Room] = 1;
            }
        }
    }
}

/// <summary>Small extension so the shutdown path is exercised from the cap tests too.</summary>
internal static class VoiceRoomDispatcherTestExtensions
{
    /// <summary>Runs one dispatcher loop iteration purely to drive its drain, then returns.</summary>
    public static async Task ShutdownAsync(this VoiceRoomDispatcher dispatcher)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await dispatcher.RunAsync(cts.Token);
    }
}
