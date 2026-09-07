using System.Threading.Channels;
using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The BOUNDS on one dispatched room. Each of these is a way a room could otherwise hold a
/// process-wide model lease (or a room slot) for ever: a caller who walks away, a turn that
/// wedges inside the model, a turn that throws, a publish that never returns, and shutdown
/// arriving mid-turn. All driven with fakes at the same seam the production code fakes —
/// <see cref="ILiveKitMediaSession"/> — so none of it needs a LiveKit server.
/// </summary>
public sealed class VoiceRoomSessionTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid UserA = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid UserB = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ATurn_IsServed_AndTheRoomIsLeftWhenItCloses()
    {
        var agent = new RecordingAgent { Reply = new byte[] { 7, 7 } };
        var media = new ScriptedMediaSession();
        media.Enqueue(new byte[] { 1 });
        media.Close();

        var reason = await RunAsync(media, agent, Grant(TenantA, UserA), Limits());

        Assert.Equal(VoiceRoomSessionEndReason.RoomClosed, reason);
        Assert.True(media.Joined);
        Assert.True(media.Left);
        Assert.Equal(new byte[] { 7, 7 }, Assert.Single(media.Published));
    }

    [Fact]
    public async Task AnIdleRoom_IsReclaimed_AndLeft()
    {
        // The caller stopped talking (or walked away). While the agent is in the room the room
        // is never empty, so LiveKit's own empty-room timeout cannot fire: this is the ONLY
        // thing that gives the slot back.
        var agent = new RecordingAgent();
        var media = new ScriptedMediaSession { BlockForever = true };

        var reason = await RunAsync(
            media, agent, Grant(TenantA, UserA),
            Limits() with { IdleTimeout = TimeSpan.FromMilliseconds(150) });

        Assert.Equal(VoiceRoomSessionEndReason.Idle, reason);
        Assert.True(media.Left);
        Assert.Equal(0, agent.TurnCount);
    }

    [Fact]
    public async Task ATurnThatWedges_IsCancelledByItsBudget_AndTheTurnGateComesBack()
    {
        // A wedged inference is the realistic way one room starves the whole process: it would
        // otherwise hold the single chat lease until the host is restarted.
        using var gate = new SemaphoreSlim(1, 1);
        var agent = new RecordingAgent { HangForever = true };
        var media = new ScriptedMediaSession();
        media.Enqueue(new byte[] { 1 });
        media.Enqueue(new byte[] { 2 });
        media.Enqueue(new byte[] { 3 });
        media.Close();

        var reason = await RunAsync(
            media, agent, Grant(TenantA, UserA),
            Limits() with { TurnBudget = TimeSpan.FromMilliseconds(120), MaxConsecutiveTurnFailures = 2 },
            gate);

        Assert.Equal(VoiceRoomSessionEndReason.TurnFailures, reason);
        Assert.True(media.Left);
        Assert.Empty(media.Published);                       // never fabricates a reply
        await WaitForAsync(() => gate.CurrentCount == 1, "the turn gate to be released");
    }

    [Fact]
    public async Task AThrowingTurn_ReleasesTheTurnGate_AndTheRoomKeepsServing()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var agent = new RecordingAgent { ThrowOnTurn = 1, Reply = new byte[] { 5 } };
        var media = new ScriptedMediaSession();
        media.Enqueue(new byte[] { 1 });     // throws
        media.Enqueue(new byte[] { 2 });     // served normally
        media.Close();

        var reason = await RunAsync(media, agent, Grant(TenantA, UserA), Limits(), gate);

        Assert.Equal(VoiceRoomSessionEndReason.RoomClosed, reason);
        Assert.Equal(2, agent.TurnCount);
        Assert.Single(media.Published);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ConsecutiveFailures_DropTheRoom_RatherThanSpinningOnIt()
    {
        var agent = new RecordingAgent { ThrowAlways = true };
        var media = new ScriptedMediaSession();
        for (var i = 0; i < 50; i++) media.Enqueue(new byte[] { 1 });
        media.Close();

        var reason = await RunAsync(
            media, agent, Grant(TenantA, UserA),
            Limits() with { MaxConsecutiveTurnFailures = 3 });

        Assert.Equal(VoiceRoomSessionEndReason.TurnFailures, reason);
        Assert.Equal(3, agent.TurnCount);   // gave up after three, did not chew through all 50
        Assert.True(media.Left);
    }

    [Fact]
    public async Task APublishThatWedges_IsBounded_AndTheRoomIsStillLeft()
    {
        var agent = new RecordingAgent { Reply = new byte[] { 9 } };
        var media = new ScriptedMediaSession { PublishBlocksForever = true };
        media.Enqueue(new byte[] { 1 });
        media.Enqueue(new byte[] { 2 });
        media.Close();

        var reason = await RunAsync(
            media, agent, Grant(TenantA, UserA),
            Limits() with
            {
                PublishBudget = TimeSpan.FromMilliseconds(120),
                MaxConsecutiveTurnFailures = 2
            });

        Assert.Equal(VoiceRoomSessionEndReason.TurnFailures, reason);
        Assert.True(media.Left);
    }

    [Fact]
    public async Task Cancellation_MidTurn_LeavesTheRoom_AndDoesNotHang()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var agent = new RecordingAgent { HangForever = true };
        var media = new ScriptedMediaSession();
        media.Enqueue(new byte[] { 1 });

        using var cts = new CancellationTokenSource();
        var session = RunAsync(media, agent, Grant(TenantA, UserA), Limits(), gate, cts.Token);

        await WaitForAsync(() => agent.TurnCount == 1, "the turn to start");
        cts.Cancel();

        var finished = await Task.WhenAny(session, Task.Delay(Patience));
        Assert.Same(session, finished);
        Assert.Equal(VoiceRoomSessionEndReason.Cancelled, await session);

        // Teardown does NOT use the cancelled token — otherwise the agent participant is left
        // behind in a live room at every shutdown.
        Assert.True(media.Left);
        await WaitForAsync(() => gate.CurrentCount == 1, "the turn gate to be released on cancellation");
    }

    [Fact]
    public async Task AJoinThatFails_IsReported_AndNeverThrows()
    {
        var media = new ScriptedMediaSession { JoinThrows = true };

        var reason = await RunAsync(media, new RecordingAgent(), Grant(TenantA, UserA), Limits());

        Assert.Equal(VoiceRoomSessionEndReason.JoinFailed, reason);
        Assert.False(media.Joined);
        // Nothing to leave here; the runner's `await using` disposal is what releases a media
        // session that got half-way through connecting.
    }

    [Fact]
    public async Task TheTurnGate_BoundsHowManyRoomsTouchTheModelAtOnce()
    {
        // THE resource-safety property: rooms are cheap, turns are not. With Chat=1/Speech=1 in
        // this deployment, eight live rooms must still only ever put ONE turn in front of the
        // model at a time.
        using var gate = new SemaphoreSlim(1, 1);
        var agents = new List<RecordingAgent>();
        var sessions = new List<Task<VoiceRoomSessionEndReason>>();
        var peak = new PeakCounter();

        for (var i = 0; i < 8; i++)
        {
            var agent = new RecordingAgent { Peak = peak, TurnDelay = TimeSpan.FromMilliseconds(15) };
            agents.Add(agent);
            var media = new ScriptedMediaSession();
            for (var utterance = 0; utterance < 3; utterance++) media.Enqueue(new byte[] { 1 });
            media.Close();
            var userId = Guid.Parse($"11111111-0000-0000-0000-{i:D12}");
            sessions.Add(RunAsync(media, agent, Grant(TenantA, userId), Limits(), gate));
        }

        await Task.WhenAll(sessions);

        Assert.All(sessions, session => Assert.Equal(VoiceRoomSessionEndReason.RoomClosed, session.Result));
        Assert.Equal(24, agents.Sum(agent => agent.TurnCount));
        Assert.Equal(1, peak.Peak);          // never two turns in the model at once
        Assert.Equal(1, gate.CurrentCount);  // and the gate is fully returned
    }

    [Fact]
    public async Task EveryTurn_CarriesTheOwningTenantAndUser()
    {
        var agent = new RecordingAgent { Reply = new byte[] { 1 } };
        var media = new ScriptedMediaSession();
        media.Enqueue(new byte[] { 1 });
        media.Enqueue(new byte[] { 2 });
        media.Close();
        var grant = Grant(TenantA, UserA);

        await RunAsync(media, agent, grant, Limits());

        Assert.Equal(2, agent.Contexts.Count);
        Assert.All(agent.Contexts, context =>
        {
            Assert.Equal(TenantA, context.TenantId);
            Assert.Equal(UserA, context.UserId);
            Assert.Equal(grant.Room, context.Room);
        });
    }

    [Fact]
    public async Task TwoTenantsRooms_NeverSeeEachOthersTurnContext()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var agentA = new RecordingAgent();
        var agentB = new RecordingAgent();
        var mediaA = new ScriptedMediaSession();
        var mediaB = new ScriptedMediaSession();
        for (var i = 0; i < 5; i++) { mediaA.Enqueue(new byte[] { 1 }); mediaB.Enqueue(new byte[] { 2 }); }
        mediaA.Close();
        mediaB.Close();

        var grantA = Grant(TenantA, UserA);
        var grantB = Grant(TenantB, UserB);
        await Task.WhenAll(
            RunAsync(mediaA, agentA, grantA, Limits(), gate),
            RunAsync(mediaB, agentB, grantB, Limits(), gate));

        Assert.Equal(5, agentA.Contexts.Count);
        Assert.Equal(5, agentB.Contexts.Count);
        Assert.All(agentA.Contexts, context => Assert.Equal(TenantA, context.TenantId));
        Assert.All(agentB.Contexts, context => Assert.Equal(TenantB, context.TenantId));
        Assert.All(agentA.Contexts, context => Assert.Equal(grantA.Room, context.Room));
        Assert.All(agentB.Contexts, context => Assert.Equal(grantB.Room, context.Room));
        Assert.NotEqual(grantA.Room, grantB.Room);
    }

    // ── the model lease itself ──

    [Fact]
    public async Task AFaultingTurn_ReleasesTheModelInferenceLease()
    {
        // Mirrors the production shape: LmKitVoiceTurnStt and AgentVoiceTurnLlm each take the
        // process-wide lease with `await using`. A turn that blows up between them must give it
        // back, or the very first failure wedges every chat request in the process.
        using var modelLease = new SemaphoreSlim(1, 1);
        var agent = new VoiceRoomAgent(
            new LeasingStt(modelLease, "hello"),
            new LeasingThrowingLlm(modelLease),
            new NoOpSynthesizer(),
            NullLogger<VoiceRoomAgent>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.RunTurnAsync(new VoiceTurnContext { InboundAudio = new byte[] { 1 } }));

        Assert.Equal(1, modelLease.CurrentCount);
    }

    [Fact]
    public async Task ACancelledTurn_ReleasesTheModelInferenceLease()
    {
        using var modelLease = new SemaphoreSlim(1, 1);
        using var cts = new CancellationTokenSource();
        var agent = new VoiceRoomAgent(
            new LeasingStt(modelLease, "hello"),
            new LeasingCancellingLlm(modelLease, cts),
            new NoOpSynthesizer(),
            NullLogger<VoiceRoomAgent>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.RunTurnAsync(new VoiceTurnContext { InboundAudio = new byte[] { 1 } }, cts.Token));

        Assert.Equal(1, modelLease.CurrentCount);
    }

    // ── helpers ──

    private static VoiceRoomSessionLimits Limits() => new()
    {
        IdleTimeout = TimeSpan.FromSeconds(5),
        TurnBudget = TimeSpan.FromSeconds(5),
        PublishBudget = TimeSpan.FromSeconds(5),
        LeaveBudget = TimeSpan.FromSeconds(5),
        MaxConsecutiveTurnFailures = 3
    };

    private static VoiceAgentGrant Grant(Guid tenantId, Guid userId)
    {
        Assert.True(VoiceRoomNaming.TryScopedRoom(tenantId, userId, "omni-room", out var room, out _));
        return new VoiceAgentGrant
        {
            Room = room,
            TenantId = tenantId,
            UserId = userId,
            Label = "omni-room",
            Voice = "alto",
            GrantedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
    }

    private static Task<VoiceRoomSessionEndReason> RunAsync(
        ILiveKitMediaSession media,
        IVoiceRoomAgent agent,
        VoiceAgentGrant grant,
        VoiceRoomSessionLimits limits,
        SemaphoreSlim? gate = null,
        CancellationToken ct = default) =>
        VoiceRoomSession.RunAsync(
            media,
            agent,
            new VoiceRoomOptions { Room = grant.Room, Voice = grant.Voice, Identity = "voice-agent" },
            grant,
            limits,
            gate,
            NullLogger.Instance,
            ct);

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

    private sealed class PeakCounter
    {
        private int _current;
        private int _peak;
        public int Peak => Volatile.Read(ref _peak);

        public IDisposable Enter()
        {
            var now = Interlocked.Increment(ref _current);
            int peak;
            while (now > (peak = Volatile.Read(ref _peak))
                   && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
            {
                // retry
            }
            return new Exit(this);
        }

        private sealed class Exit : IDisposable
        {
            private readonly PeakCounter _owner;
            public Exit(PeakCounter owner) => _owner = owner;
            public void Dispose() => Interlocked.Decrement(ref _owner._current);
        }
    }

    /// <summary>A turn agent under the test's control; records every context it was handed.</summary>
    private sealed class RecordingAgent : IVoiceRoomAgent
    {
        private readonly List<VoiceTurnContext> _contexts = new();
        private readonly object _lock = new();
        private int _turns;

        public byte[] Reply { get; init; } = Array.Empty<byte>();
        public bool HangForever { get; init; }
        public bool ThrowAlways { get; init; }
        public int ThrowOnTurn { get; init; }
        public TimeSpan TurnDelay { get; init; }
        public PeakCounter? Peak { get; init; }

        public int TurnCount => Volatile.Read(ref _turns);
        public IReadOnlyList<VoiceTurnContext> Contexts
        {
            get { lock (_lock) return _contexts.ToList(); }
        }

        public async Task<VoiceTurnResult> RunTurnAsync(VoiceTurnContext context, CancellationToken ct = default)
        {
            var turn = Interlocked.Increment(ref _turns);
            lock (_lock) _contexts.Add(context);

            using var _ = Peak?.Enter();
            if (HangForever) await Task.Delay(Timeout.Infinite, ct);
            if (TurnDelay > TimeSpan.Zero) await Task.Delay(TurnDelay, ct);
            if (ThrowAlways || turn == ThrowOnTurn) throw new InvalidOperationException("turn blew up");

            return Reply.Length > 0
                ? VoiceTurnResult.Reply("heard", "said", Reply)
                : VoiceTurnResult.Empty("no-speech-detected");
        }
    }

    /// <summary>
    /// A media session under the test's control. Utterances are queued; <see cref="Close"/>
    /// makes the next read report the room closed. Every operation honours its token.
    /// </summary>
    private sealed class ScriptedMediaSession : ILiveKitMediaSession
    {
        private readonly Channel<byte[]?> _utterances = Channel.CreateUnbounded<byte[]?>();
        private readonly List<byte[]> _published = new();
        private readonly object _lock = new();

        public bool JoinThrows { get; init; }
        public bool BlockForever { get; init; }
        public bool PublishBlocksForever { get; init; }

        public bool Joined { get; private set; }
        public bool Left { get; private set; }
        public IReadOnlyList<byte[]> Published
        {
            get { lock (_lock) return _published.ToList(); }
        }

        public void Enqueue(byte[] utterance) => _utterances.Writer.TryWrite(utterance);
        public void Close() => _utterances.Writer.TryWrite(null);

        public Task JoinAsync(VoiceRoomOptions options, CancellationToken ct = default)
        {
            if (JoinThrows) throw new InvalidOperationException("LiveKit refused the join");
            Joined = true;
            return Task.CompletedTask;
        }

        public async Task<ReadOnlyMemory<byte>?> ReadUtteranceAsync(CancellationToken ct = default)
        {
            if (BlockForever)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }
            var utterance = await _utterances.Reader.ReadAsync(ct);
            if (utterance is null) return null;
            return new ReadOnlyMemory<byte>(utterance);
        }

        public async Task PublishAsync(ReadOnlyMemory<byte> wavAudio, CancellationToken ct = default)
        {
            if (PublishBlocksForever)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return;
            }
            lock (_lock) _published.Add(wavAudio.ToArray());
        }

        public Task LeaveAsync(CancellationToken ct = default)
        {
            Left = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Takes the shared model lease and gives it back, exactly like LmKitVoiceTurnStt.</summary>
    private sealed class LeasingStt : IVoiceTurnStt
    {
        private readonly SemaphoreSlim _lease;
        private readonly string _transcript;
        public LeasingStt(SemaphoreSlim lease, string transcript) { _lease = lease; _transcript = transcript; }

        public async Task<string> TranscribeAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
        {
            await using var lease = await Lease.TakeAsync(_lease, ct);
            return _transcript;
        }
    }

    /// <summary>Takes the lease, then throws — the failure mode that used to wedge the process.</summary>
    private sealed class LeasingThrowingLlm : IVoiceTurnLlm
    {
        private readonly SemaphoreSlim _lease;
        public LeasingThrowingLlm(SemaphoreSlim lease) => _lease = lease;

        public async Task<string> RespondAsync(string userUtterance, CancellationToken ct = default)
        {
            await using var lease = await Lease.TakeAsync(_lease, ct);
            throw new InvalidOperationException("inference failed");
        }
    }

    /// <summary>Takes the lease, then observes cancellation from inside the native call.</summary>
    private sealed class LeasingCancellingLlm : IVoiceTurnLlm
    {
        private readonly SemaphoreSlim _lease;
        private readonly CancellationTokenSource _cts;
        public LeasingCancellingLlm(SemaphoreSlim lease, CancellationTokenSource cts) { _lease = lease; _cts = cts; }

        public async Task<string> RespondAsync(string userUtterance, CancellationToken ct = default)
        {
            await using var lease = await Lease.TakeAsync(_lease, ct);
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return "unreachable";
        }
    }

    /// <summary>The <c>IAsyncDisposable</c> lease shape LmModelManager hands out.</summary>
    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private Lease(SemaphoreSlim gate) => _gate = gate;

        public static async Task<Lease> TakeAsync(SemaphoreSlim gate, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Lease(gate);
        }

        public ValueTask DisposeAsync()
        {
            _gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpSynthesizer : ISpeechSynthesizer
    {
        public bool IsAvailable => false;
        public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }
}

/// <summary>
/// <c>VoiceBudget</c> is the primitive every wait in a dispatched room goes through. Its
/// contract matters: a budget overrun must be reported, not thrown, and a real shutdown must
/// never be mistaken for one.
/// </summary>
public sealed class VoiceBudgetTests
{
    [Fact]
    public async Task AStepThatFinishesInTime_ReportsItsValue()
    {
        var (completed, value) = await VoiceBudget.RunAsync(
            _ => Task.FromResult(42), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(completed);
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task AStepThatOverruns_IsReportedAsIncomplete_NotThrown()
    {
        var (completed, value) = await VoiceBudget.RunAsync(
            async token => { await Task.Delay(Timeout.Infinite, token); return 1; },
            TimeSpan.FromMilliseconds(80),
            CancellationToken.None);

        Assert.False(completed);
        Assert.Equal(0, value);
    }

    [Fact]
    public async Task ShutdownIsNeverMistakenForATimeout()
    {
        using var cts = new CancellationTokenSource();
        var running = VoiceBudget.RunAsync(
            async token => { await Task.Delay(Timeout.Infinite, token); return 1; },
            TimeSpan.FromSeconds(30),
            cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task AStepThatThrows_PropagatesItsOwnFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => VoiceBudget.RunAsync(
            _ => Task.FromException<int>(new InvalidOperationException("boom")),
            TimeSpan.FromSeconds(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task AnUnboundedBudget_JustRunsTheStep()
    {
        var (completed, value) = await VoiceBudget.RunAsync(
            _ => Task.FromResult(7), Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.True(completed);
        Assert.Equal(7, value);
    }
}
