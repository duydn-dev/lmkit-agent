using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Approvals;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Application.Approvals.Handlers;
using LmKitOmniApi.Application.Approvals.Queries;
using LmKitOmniApi.Application.ComputerUse.Commands;
using LmKitOmniApi.Application.ComputerUse.Handlers;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The computer-use approval ROW lifecycle, over a real (SQLite) database.
///
/// <para><b>The defect.</b> <see cref="ComputerUseApprovalGate"/> inserts a
/// <c>task_approvals</c> row and waits ~90 seconds for a human. When that wait elapsed it
/// simply returned false and walked away, leaving the row <c>Pending</c> — permanently
/// visible in the user's approval list, long after the loop that raised it had refused the
/// action and moved on. Clicking "approve" on such a row did not resume anything: it
/// routed into the GENERIC approve handler, which asked the tool dispatcher to execute
/// <c>COMPUTER_USE</c>, an action no role is permitted and no dispatcher case can perform.
/// The 24-hour expiry sweeper eventually collected those rows, but that is garbage
/// collection, not a resolution.</para>
///
/// <para>These tests pin the resolution instead: every exit path closes its own row, the
/// close is a conditional claim so a human decision landing at the same moment still wins,
/// the row stops being offered, and the generic approve endpoint now RECORDS a
/// computer-use decision rather than pretending to execute a tool.</para>
/// </summary>
// Deliberately NOT in the "DbSqlite" collection: that collection serializes the classes
// that open real SQLite FILES. This one owns a private ":memory:" connection and shares
// nothing. Every test here is single-threaded — the one "a human answered mid-wait" case
// drives the decision from inside the gate's own scope factory rather than from a
// background writer, so nothing races the shared connection and nothing depends on
// machine speed.
public sealed class ComputerUseApprovalGateLifecycleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly TaskApprovalPayloadProtector _protector = new(new EphemeralDataProtectionProvider());

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    public ComputerUseApprovalGateLifecycleTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<HermesDbContext>(options => options.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T" });
        db.Users.Add(new User
        {
            Id = _userId,
            TenantId = _tenantId,
            Username = "u",
            Email = "u@t.test",
            PasswordHash = "x",
            Role = "User"
        });
        db.SaveChanges();
    }

    // ── 1. the zombie, gone ──────────────────────────────────────────────────

    /// <summary>
    /// The wait elapses, the action is refused (fail-closed, unchanged) AND the row the
    /// gate created is resolved to a terminal status with a reason and a resolution
    /// timestamp.
    /// </summary>
    [Fact]
    public async Task WhenTheWaitElapses_TheGateRefusesAndResolvesItsOwnRow()
    {
        var (gate, request) = NewGate();

        var approved = await gate.RequestAsync(request, CancellationToken.None);

        Assert.False(approved);

        var row = Read(request.ApprovalId);
        Assert.NotNull(row);
        Assert.Equal(ComputerUseApprovalGate.TimedOutStatus, row!.Status);
        Assert.Equal(ComputerUseApprovalGate.TimedOutComment, row.RejectionComment);
        Assert.NotNull(row.ResolvedAtUtc);
    }

    /// <summary>
    /// The poll interval is derived from the configured budget, so a short budget is
    /// actually honoured instead of being overshot by a fixed 2-second sleep. The SHIPPED
    /// budget must land on exactly the interval that shipped before — a production timing
    /// change is not something this round is making.
    /// </summary>
    [Fact]
    public void ThePollIntervalIsUnchangedAtTheShippedBudget()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            ComputerUseApprovalGate.PollIntervalFor(new ComputerUseOptions().ApprovalTimeoutSeconds));

        // Never longer than a quarter of the budget, and never a hot loop.
        foreach (var budget in new[] { int.MinValue, 0, 1, 2, 4, 8, 16, 90, 3600 })
        {
            var interval = ComputerUseApprovalGate.PollIntervalFor(budget);
            Assert.InRange(interval, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            Assert.True(interval <= TimeSpan.FromSeconds(Math.Max(1, budget)) / 4 || interval == TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// The terminal status the gate writes is the one the rest of the product already
    /// understands, rather than a private word only this file knows.
    /// </summary>
    [Fact]
    public void TheTimeoutStatus_IsTheSameVocabularyTheSweeperUses()
    {
        Assert.Equal(TaskApproval.ExpiredStatus, ComputerUseApprovalGate.TimedOutStatus);
        Assert.Equal(TaskApproval.CancelledStatus, ComputerUseApprovalGate.AbandonedStatus);
        // Status is MaxLength(32) — a longer word would be silently truncated on SQLite
        // and would throw on PostgreSQL.
        Assert.InRange(ComputerUseApprovalGate.TimedOutStatus.Length, 1, 32);
        Assert.InRange(ComputerUseApprovalGate.AbandonedStatus.Length, 1, 32);
        // RejectionComment is MaxLength(1024).
        Assert.InRange(ComputerUseApprovalGate.TimedOutComment.Length, 1, 1024);
        Assert.InRange(ComputerUseApprovalGate.AbandonedComment.Length, 1, 1024);
    }

    /// <summary>
    /// The user-visible consequence, through the SHIPPING pending-approvals handler: a
    /// timed-out row is no longer offered. A row that fails this assertion is a row
    /// sitting in someone's approval list for the next 24 hours.
    /// </summary>
    [Fact]
    public async Task AfterTheTimeout_TheRowIsNoLongerOfferedForApproval()
    {
        var (gate, request) = NewGate();

        await gate.RequestAsync(request, CancellationToken.None);

        var pending = await new GetPendingApprovalsQueryHandler(NewContext(), _protector).Handle(
            new GetPendingApprovalsQuery { TenantId = _tenantId, UserId = _userId }, CancellationToken.None);

        Assert.Empty(pending);
    }

    /// <summary>While the gate IS waiting, the row is genuinely offered — the timeout is what removes it, not the row type.</summary>
    [Fact]
    public async Task DuringTheWait_TheRowIsOfferedForApproval()
    {
        var request = NewRequest();
        List<PendingApprovalDto> seenWhileWaiting = [];

        // Runs inside the gate's second scope creation, i.e. after the row exists and
        // before the first status read.
        await RunWithDecisionDuringWait(request, onSecondScope: () =>
        {
            seenWhileWaiting = new GetPendingApprovalsQueryHandler(NewContext(), _protector).Handle(
                new GetPendingApprovalsQuery { TenantId = _tenantId, UserId = _userId },
                CancellationToken.None).GetAwaiter().GetResult();
            Reject(request.ApprovalId);
        });

        var offered = Assert.Single(seenWhileWaiting);
        Assert.Equal(TaskApproval.ComputerUseActionName, offered.ActionName);
        Assert.Equal(request.Details, offered.Details);
    }

    /// <summary>
    /// The other abandonment path: the computer-use run is cancelled while a request is
    /// pending. The row belonged to that run, so it dies with it instead of outliving it.
    /// </summary>
    [Fact]
    public async Task WhenTheRunIsCancelledMidWait_TheRowIsClosedToo()
    {
        var (gate, request) = NewGate(timeoutSeconds: 60);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var approved = await gate.RequestAsync(request, cts.Token);

        Assert.False(approved);
        var row = Read(request.ApprovalId);
        Assert.Equal(ComputerUseApprovalGate.AbandonedStatus, row!.Status);
        Assert.Equal(ComputerUseApprovalGate.AbandonedComment, row.RejectionComment);
        Assert.NotNull(row.ResolvedAtUtc);
    }

    /// <summary>
    /// A resolved row can no longer be claimed by ANY approval handler: both of them claim
    /// on <c>Status = 'Pending'</c>, so the honest answer for "this request is no longer
    /// answerable" is Conflict.
    /// </summary>
    [Fact]
    public async Task AfterTheTimeout_ApproveAndRejectBothRefuse()
    {
        var (gate, request) = NewGate();
        await gate.RequestAsync(request, CancellationToken.None);

        var orchestrator = new CountingOrchestrator();
        var approve = await ApproveHandler(orchestrator).Handle(
            new ApproveTaskCommand { TaskId = request.ApprovalId, TenantId = _tenantId, UserId = _userId },
            CancellationToken.None);
        var reject = await new RejectTaskCommandHandler(
                NewContext(), _protector, NullLogger<RejectTaskCommandHandler>.Instance)
            .Handle(
                new RejectTaskCommand { TaskId = request.ApprovalId, TenantId = _tenantId, UserId = _userId },
                CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Conflict, approve.Outcome);
        Assert.False(reject);
        Assert.Equal(0, orchestrator.Calls);
        Assert.Equal(ComputerUseApprovalGate.TimedOutStatus, Read(request.ApprovalId)!.Status);
    }

    // ── 2. the claim is a claim, not a blind write ───────────────────────────

    /// <summary>
    /// A decision that landed while the deadline was passing is already recorded, so the
    /// gate reports it back instead of overwriting it with its own timeout status.
    ///
    /// <para>Exercised directly rather than through the polling loop on purpose: driving
    /// it end to end means racing a background writer against a wall-clock deadline, which
    /// on a loaded machine decides the assertion instead of the code under test.</para>
    /// </summary>
    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("Executing")]
    public async Task ADecisionThatLandedFirst_IsReportedBackAndNotOverwritten(string decision)
    {
        var (gate, request) = NewGate();
        SeedApproval(request, decision);

        var raced = await gate.ResolveIfStillPendingAsync(
            request, ComputerUseApprovalGate.TimedOutStatus, ComputerUseApprovalGate.TimedOutComment);

        Assert.Equal(decision, raced);
        Assert.Equal(decision, Read(request.ApprovalId)!.Status);
    }

    /// <summary>The other side of the claim: a still-Pending row IS closed, and the caller is told it won.</summary>
    [Fact]
    public async Task AStillPendingRow_IsClaimedAndTheCallerIsToldItWon()
    {
        var (gate, request) = NewGate();
        SeedApproval(request, "Pending");

        var raced = await gate.ResolveIfStillPendingAsync(
            request, ComputerUseApprovalGate.TimedOutStatus, ComputerUseApprovalGate.TimedOutComment);

        Assert.Null(raced);
        Assert.Equal(ComputerUseApprovalGate.TimedOutStatus, Read(request.ApprovalId)!.Status);
    }

    /// <summary>
    /// The claim is scoped exactly like the dedicated resolve endpoint's — same tenant,
    /// same user, <c>ActionName = COMPUTER_USE</c> — so it can never close some other
    /// tool's approval.
    /// </summary>
    [Theory]
    [InlineData("DBWRITE")]
    [InlineData("MCP:server:tool")]
    public async Task TheClaimNeverTouchesANonComputerUseApproval(string actionName)
    {
        var (gate, request) = NewGate();
        SeedApproval(request, "Pending", actionName: actionName);

        var raced = await gate.ResolveIfStillPendingAsync(
            request, ComputerUseApprovalGate.TimedOutStatus, ComputerUseApprovalGate.TimedOutComment);

        Assert.Equal("Pending", raced);
        Assert.Equal("Pending", Read(request.ApprovalId)!.Status);
    }

    /// <summary>
    /// End to end and DETERMINISTICALLY: the human answers while the gate is waiting, the
    /// gate's next poll sees it, and the answer decides the outcome — the timeout claim
    /// never gets to overwrite it. The decision is applied from inside the gate's own
    /// scope factory, so this is single-threaded and independent of machine speed.
    /// </summary>
    [Theory]
    [InlineData(true, "Approved")]
    [InlineData(false, "Rejected")]
    public async Task ADecisionMadeDuringTheWait_DecidesTheOutcome(bool approve, string expectedStatus)
    {
        var request = NewRequest();

        var decided = await RunWithDecisionDuringWait(request, onSecondScope: () =>
        {
            if (approve) ApproveThroughTheDedicatedEndpoint(request.ApprovalId);
            else Reject(request.ApprovalId);
        });

        Assert.Equal(approve, decided);
        Assert.Equal(expectedStatus, Read(request.ApprovalId)!.Status);
    }

    // ── 3. the generic approve endpoint stops lying ──────────────────────────

    /// <summary>
    /// The dispatcher half of the defect. The generic approve endpoint used to resolve a
    /// computer-use row by asking the orchestrator to EXECUTE <c>COMPUTER_USE</c> — which
    /// no role permits and no dispatcher case can perform, so the handler's catch wrote
    /// <c>Failed</c>, which the waiting gate reads as a REJECTION. Clicking "approve"
    /// rejected the action. It now records the decision the gate is actually polling for,
    /// and executes nothing.
    /// </summary>
    [Fact]
    public async Task GenericApprove_RecordsTheDecision_AndNeverCallsTheOrchestrator()
    {
        var request = NewRequest();
        SeedApproval(request, "Pending");
        var orchestrator = new CountingOrchestrator();

        var result = await ApproveHandler(orchestrator).Handle(
            new ApproveTaskCommand { TaskId = request.ApprovalId, TenantId = _tenantId, UserId = _userId },
            CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Recorded, result.Outcome);
        Assert.Equal(0, orchestrator.Calls);
        // "Approved" is exactly the word the gate's ApprovedStatuses set waits for.
        Assert.Equal("Approved", Read(request.ApprovalId)!.Status);
        Assert.NotNull(Read(request.ApprovalId)!.ResolvedAtUtc);
    }

    /// <summary>Approving twice cannot both win — the second is an ordinary lost race.</summary>
    [Fact]
    public async Task GenericApprove_OfAnAlreadyResolvedComputerUseRow_IsAConflict()
    {
        var request = NewRequest();
        SeedApproval(request, "Pending");
        var orchestrator = new CountingOrchestrator();
        var command = new ApproveTaskCommand { TaskId = request.ApprovalId, TenantId = _tenantId, UserId = _userId };

        Assert.Equal(ApproveTaskOutcome.Recorded,
            (await ApproveHandler(orchestrator).Handle(command, CancellationToken.None)).Outcome);
        Assert.Equal(ApproveTaskOutcome.Conflict,
            (await ApproveHandler(orchestrator).Handle(command, CancellationToken.None)).Outcome);
        Assert.Equal(0, orchestrator.Calls);
    }

    /// <summary>
    /// An ordinary (non computer-use) approval is untouched by the short-circuit: it still
    /// goes through the orchestrator exactly as before.
    /// </summary>
    [Fact]
    public async Task GenericApprove_OfAnOrdinaryApproval_StillExecutesTheTool()
    {
        var request = NewRequest();
        SeedApproval(request, "Pending", actionName: "DBWRITE");
        var orchestrator = new CountingOrchestrator { Output = "1 row deleted" };

        var result = await ApproveHandler(orchestrator).Handle(
            new ApproveTaskCommand { TaskId = request.ApprovalId, TenantId = _tenantId, UserId = _userId },
            CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, result.Outcome);
        Assert.Equal("1 row deleted", result.Result);
        Assert.Equal(1, orchestrator.Calls);
    }

    // ── 4. the gate and the expiry sweeper still cannot race ─────────────────

    /// <summary>
    /// The invariant the previous round deliberately created and this change must not
    /// break: the durable deadline is clamped to at least an HOUR, while the gate's
    /// in-process wait is ~90 seconds, so the gate always resolves its own row first and
    /// the sweeper only ever meets rows that are already terminal.
    /// </summary>
    [Fact]
    public void TheDurableDeadlineStaysOrdersOfMagnitudeBeyondTheGatesWait()
    {
        var gateWait = TimeSpan.FromSeconds(new ComputerUseOptions().ApprovalTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(90), gateWait);

        // Shipped default...
        Assert.True(new ApprovalExpiryOptions().TimeToLive > gateWait);
        // ...and the floor an operator cannot configure below, which is the value that
        // actually makes the race impossible.
        foreach (var configured in new[] { int.MinValue, -1, 0, 1 })
            Assert.Equal(TimeSpan.FromHours(1),
                new ApprovalExpiryOptions { TimeToLiveHours = configured }.TimeToLive);
        Assert.True(new ApprovalExpiryOptions { TimeToLiveHours = 0 }.TimeToLive > gateWait * 39);
    }

    /// <summary>
    /// The same property at the row level: a row the gate just created is nowhere near its
    /// durable deadline, so a sweep running at that instant leaves it alone — the gate is
    /// the only thing that can resolve it while the loop is still listening.
    /// </summary>
    [Fact]
    public async Task ASweepRunningWhileTheGateWaits_LeavesTheRowAlone()
    {
        var request = NewRequest();
        SeedApproval(request, "Pending");

        var row = Read(request.ApprovalId)!;
        Assert.True(row.ExpiresAtUtc - DateTime.UtcNow > TimeSpan.FromHours(1));

        var swept = await new ApprovalExpirySweeper(
                NewContext(), _protector, Options.Create(new ApprovalExpiryOptions()),
                NullLogger<ApprovalExpirySweeper>.Instance)
            .SweepAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(0, swept);
        Assert.Equal("Pending", Read(request.ApprovalId)!.Status);
    }

    /// <summary>
    /// And the reverse, defensively: if a sweeper ever DID win (an operator configuring the
    /// gate's wait past the one-hour floor is the only way), the gate recognises the
    /// sweeper's terminal status and fails closed promptly instead of polling out its
    /// whole budget against a row nobody can answer any more.
    /// </summary>
    [Fact]
    public async Task IfTheSweeperEverWon_TheGateReadsItAsADenial()
    {
        var request = NewRequest();

        var decided = await RunWithDecisionDuringWait(request, onSecondScope: () =>
        {
            using var db = NewContext();
            db.TaskApprovals.Where(t => t.Id == request.ApprovalId)
                .ExecuteUpdate(setters => setters
                    .SetProperty(t => t.Status, TaskApproval.ExpiredStatus)
                    .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow));
        });

        Assert.False(decided);
        Assert.Equal(TaskApproval.ExpiredStatus, Read(request.ApprovalId)!.Status);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private (ComputerUseApprovalGate Gate, ComputerUseApprovalRequest Request) NewGate(int timeoutSeconds = 1)
        => (NewGate(_provider.GetRequiredService<IServiceScopeFactory>(), timeoutSeconds), NewRequest());

    private ComputerUseApprovalGate NewGate(IServiceScopeFactory scopeFactory, int timeoutSeconds)
        => new(
            scopeFactory,
            _protector,
            Options.Create(new ComputerUseOptions { ApprovalTimeoutSeconds = timeoutSeconds }),
            NullLogger<ComputerUseApprovalGate>.Instance);

    private ComputerUseApprovalRequest NewRequest() => new(
        Guid.NewGuid(), _tenantId, _userId, _sessionId,
        "click #submit", "{\"action\":\"click\",\"ref\":\"ref_3\"}");

    /// <summary>
    /// Runs the gate with <paramref name="onSecondScope"/> invoked at the gate's SECOND
    /// scope creation — i.e. after <c>CreatePendingApprovalAsync</c> has written the row
    /// and immediately before the first status read. Single-threaded: the callback runs on
    /// the gate's own execution path, so nothing shares the SQLite connection concurrently
    /// and no assertion here depends on a wall clock.
    /// </summary>
    private Task<bool> RunWithDecisionDuringWait(ComputerUseApprovalRequest request, Action onSecondScope)
    {
        var factory = new DecidingScopeFactory(
            _provider.GetRequiredService<IServiceScopeFactory>(), onCall: 2, onSecondScope);
        // A 4-second budget polls every second, so the decision is observed on the first
        // poll and the test costs about a second — while still leaving three further polls
        // of slack, so a scheduling stall cannot turn this into a spurious timeout.
        return NewGate(factory, timeoutSeconds: 4).RequestAsync(request, CancellationToken.None);
    }

    /// <summary>The dedicated computer-use resolve path, which is what a real approval click drives.</summary>
    private void ApproveThroughTheDedicatedEndpoint(Guid approvalId)
    {
        var outcome = new ResolveComputerUseApprovalCommandHandler(NewContext()).Handle(
            new ResolveComputerUseApprovalCommand
            {
                ApprovalId = approvalId,
                TenantId = _tenantId,
                UserId = _userId,
                Approve = true
            }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(ResolveComputerUseApprovalOutcome.Resolved, outcome);
    }

    private void Reject(Guid approvalId)
    {
        var outcome = new ResolveComputerUseApprovalCommandHandler(NewContext()).Handle(
            new ResolveComputerUseApprovalCommand
            {
                ApprovalId = approvalId,
                TenantId = _tenantId,
                UserId = _userId,
                Approve = false,
                Comment = "Không"
            }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(ResolveComputerUseApprovalOutcome.Resolved, outcome);
    }

    private ApproveTaskCommandHandler ApproveHandler(CountingOrchestrator orchestrator)
        => new(NewContext(), orchestrator, _protector, NullLogger<ApproveTaskCommandHandler>.Instance);

    /// <summary>Writes the row the gate would have written, so the claim can be tested without one.</summary>
    private void SeedApproval(
        ComputerUseApprovalRequest request, string status, string? actionName = null)
    {
        using var db = NewContext();
        if (!db.ChatSessions.Any(s => s.Id == request.SessionId))
        {
            db.ChatSessions.Add(new ChatSession
            {
                Id = request.SessionId,
                TenantId = request.TenantId,
                UserId = request.UserId,
                Title = "Computer-use",
                IsAgentRun = true
            });
        }
        db.TaskApprovals.Add(new TaskApproval
        {
            Id = request.ApprovalId,
            TenantId = request.TenantId,
            UserId = request.UserId,
            ChatSessionId = request.SessionId,
            ActionName = actionName ?? TaskApproval.ComputerUseActionName,
            ParametersJson = _protector.Protect(request.Details),
            Status = status
        });
        db.SaveChanges();
    }

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private TaskApproval? Read(Guid approvalId)
    {
        using var db = NewContext();
        return db.TaskApprovals.AsNoTracking().FirstOrDefault(t => t.Id == approvalId);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Fires <c>decide</c> on the Nth <see cref="CreateScope"/> call, on the caller's own
    /// thread, then delegates. <c>CreateAsyncScope()</c> is an extension over
    /// <see cref="CreateScope"/>, so this intercepts every scope the gate opens.
    /// </summary>
    private sealed class DecidingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly int _onCall;
        private readonly Action _decide;
        private int _calls;

        public DecidingScopeFactory(IServiceScopeFactory inner, int onCall, Action decide)
        {
            _inner = inner;
            _onCall = onCall;
            _decide = decide;
        }

        public IServiceScope CreateScope()
        {
            if (++_calls == _onCall) _decide();
            return _inner.CreateScope();
        }
    }

    /// <summary>Model-free tool boundary: counts executions, so a computer-use approval
    /// that nonetheless reached the dispatcher would be impossible to miss.</summary>
    private sealed class CountingOrchestrator : IAgentOrchestrator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string Output { get; init; } = "tool output";

        public IAsyncEnumerable<string> StreamProcessQueryAsync(
            Guid tenantId, Guid sessionId, Guid userId, string userRole, string query,
            LMKit.TextGeneration.Chat.ChatHistory history, AgentRequestOptions? options,
            CancellationToken cancellationToken,
            IList<LmKitOmniApi.Infrastructure.AI.AgentRunStepData>? stepSink = null)
            => throw new NotSupportedException("Approval handling never streams.");

        public Task<string> ExecuteDirectActionAsync(
            Guid tenantId, Guid userId, string action, string query,
            Guid? approvalId = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Output);
        }
    }
}
