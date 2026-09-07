using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.Approvals;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Application.Approvals.Handlers;
using LmKitOmniApi.Application.Approvals.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// R3 — the approval DEADLINE. A <see cref="TaskApproval"/> had no expiry at all, which
/// left three holes:
///
/// <list type="number">
/// <item>an approval nobody answered stayed Pending forever, and the
/// <see cref="AgentRun"/> parked on it stayed at AwaitingApproval with a null
/// CompletedAtUtc forever — the one gap <c>AgentRunApprovalReconciler</c> could not close,
/// because closing it needs something that acts WITHOUT a human request;</item>
/// <item>the security-relevant one: <c>POST /api/taskapproval/{id}/approve</c> would
/// happily execute a side-effecting tool call proposed weeks earlier, against a payload
/// captured then — a human clicking "approve" on a stale row is signing for context that
/// is gone;</item>
/// <item>the pending list returned every Pending row ever created, so it grew without
/// bound.</item>
/// </list>
///
/// These tests pin all three over a real (SQLite) DB with the model boundary faked: an
/// overdue approval is swept to Expired and its parked run reaches a TERMINAL state with
/// CompletedAtUtc set, an expired approval can NEVER execute its tool, the pending list
/// hides overdue rows even when the sweeper never ran, and sweeping twice cannot
/// double-step a run.
/// </summary>
public sealed class ApprovalExpiryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly TaskApprovalPayloadProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly RecordingOrchestrator _orchestrator = new();

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private const string GatedAction = "DBWRITE";
    private const string GatedPayload = "DELETE FROM customers WHERE id = 1";

    public ApprovalExpiryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = NewContext();
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T" });
        _db.Users.Add(NewUser(_tenantId, _userId));
        _db.SaveChanges();
    }

    // ── 1. the sweeper closes what nobody answered ────────────────────────

    [Fact]
    public async Task Sweep_ExpiresAnOverdueApproval_AndReleasesTheRunParkedOnIt()
    {
        var (runId, sessionId, approvalId) = SeedParkedRun(expiresAtUtc: Hours(-1));

        var expired = await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(1, expired);
        // Nothing executed: an expiry is not an approval.
        Assert.Equal(0, _orchestrator.Calls);

        using var verify = NewContext();

        // The approval row is terminal and stamped with when it lapsed.
        var approval = verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId);
        Assert.Equal(TaskApproval.ExpiredStatus, approval.Status);
        Assert.NotNull(approval.ResolvedAtUtc);

        // The run left the only non-terminal resting state, with a completion time —
        // the whole point of routing the sweep through the reconciler.
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.NotEqual(AgentRunStatuses.AwaitingApproval, run.Status);
        Assert.Equal(AgentRunStatuses.Expired, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        // No tool ran, so there is no result — only a truthful explanation.
        Assert.Null(run.Result);
        Assert.False(string.IsNullOrWhiteSpace(run.Error));
        Assert.Contains("hết hạn", run.Error!);

        // The lapse is in the step timeline, carrying what was never approved.
        var steps = verify.AgentRunSteps.AsNoTracking()
            .Where(s => s.AgentRunId == runId).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(GatedAction, steps[1].Action);
        Assert.Equal(GatedPayload, steps[1].Input);
        Assert.Contains("hết hạn", steps[1].Observation);

        // And the user can read why their run stopped.
        var assistant = Assert.Single(verify.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == sessionId && m.Role == "assistant").ToList());
        Assert.Contains("KHÔNG được thực thi", assistant.Content);
    }

    [Fact]
    public async Task Sweep_LeavesAnApprovalThatIsStillWithinItsWindow_Alone()
    {
        var (runId, _, approvalId) = SeedParkedRun(expiresAtUtc: Hours(+1));

        Assert.Equal(0, await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        Assert.Equal("Pending", verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.AwaitingApproval, run.Status);
        Assert.Null(run.CompletedAtUtc);
    }

    [Fact]
    public async Task Sweep_NeverReopensAnApprovalAHumanAlreadyResolved()
    {
        var (runId, _, approvalId) = SeedParkedRun(expiresAtUtc: Hours(-1));

        // The human got there first, just before the sweep.
        Assert.True(await RejectHandler().Handle(
            new RejectTaskCommand { TaskId = approvalId, TenantId = _tenantId, UserId = _userId, Comment = "Không" },
            CancellationToken.None));

        Assert.Equal(0, await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        // A clock must never overwrite a decision.
        Assert.Equal("Rejected", verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
        Assert.Equal(AgentRunStatuses.Rejected, verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId).Status);
    }

    // ── 2. idempotence ────────────────────────────────────────────────────

    [Fact]
    public async Task SweepingTwice_CannotDoubleStepOrRescoreTheRun()
    {
        var (runId, _, _) = SeedParkedRun(expiresAtUtc: Hours(-1));

        Assert.Equal(1, await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None));
        var completedAt = CompletedAt(runId);

        // A fresh sweeper/scope, exactly like the next tick of the worker.
        Assert.Equal(0, await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Expired, run.Status);
        Assert.Equal(completedAt, run.CompletedAtUtc);
        Assert.Equal(2, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == runId));
        Assert.Equal(1, verify.ChatMessages.AsNoTracking().Count(m => m.Role == "assistant"));
    }

    // ── 3. an expired approval can never execute ──────────────────────────

    [Fact]
    public async Task Approve_AfterTheDeadline_DoesNotExecuteTheTool()
    {
        var (runId, _, approvalId) = SeedParkedRun(expiresAtUtc: Hours(-1));

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        // The security property: the side-effecting tool never ran.
        Assert.Equal(0, _orchestrator.Calls);
        Assert.Equal(ApproveTaskOutcome.Expired, outcome.Outcome);
        Assert.Null(outcome.Result);

        using var verify = NewContext();
        // Refusing is not resolving: the row stays for the sweeper, and the run is
        // untouched rather than falsely marked as if a human had decided.
        Assert.Equal("Pending", verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
        Assert.Equal(AgentRunStatuses.AwaitingApproval,
            verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId).Status);
    }

    [Fact]
    public async Task Approve_OfAnAlreadySweptApproval_DoesNotExecuteTheTool()
    {
        var (_, _, approvalId) = SeedParkedRun(expiresAtUtc: Hours(-1));
        await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None);

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(0, _orchestrator.Calls);
        // Already terminal, so this is an ordinary lost race rather than a fresh lapse.
        Assert.Equal(ApproveTaskOutcome.Conflict, outcome.Outcome);
        using var verify = NewContext();
        Assert.Equal(TaskApproval.ExpiredStatus,
            verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
    }

    [Fact]
    public async Task Approve_WithinTheWindow_StillExecutesExactlyAsBefore()
    {
        var (runId, _, approvalId) = SeedParkedRun(expiresAtUtc: Hours(+1));
        _orchestrator.Output = "1 row deleted";

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, outcome.Outcome);
        Assert.Equal("1 row deleted", outcome.Result);
        Assert.Equal(1, _orchestrator.Calls);
        using var verify = NewContext();
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval,
            verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId).Status);
    }

    // ── 4. reject: the deliberate asymmetry ───────────────────────────────

    [Fact]
    public async Task Reject_AfterTheDeadlineButBeforeTheSweep_StillRecordsTheHumansRefusal()
    {
        // A rejection executes nothing, so the deadline has nothing to protect against.
        // Refusing it would only discard a decision a human actually made and leave the
        // run parked for the sweeper — strictly worse. "Somebody said no" is also more
        // truthful than "nobody looked".
        var (runId, _, approvalId) = SeedParkedRun(expiresAtUtc: Hours(-1));

        var rejected = await RejectHandler().Handle(
            new RejectTaskCommand { TaskId = approvalId, TenantId = _tenantId, UserId = _userId, Comment = "Quá rủi ro" },
            CancellationToken.None);

        Assert.True(rejected);
        Assert.Equal(0, _orchestrator.Calls);

        using var verify = NewContext();
        Assert.Equal("Rejected", verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Rejected, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task Reject_OfAnAlreadySweptApproval_Is404_LikeAnyOtherResolvedTask()
    {
        var (runId, _, approvalId) = SeedParkedRun(expiresAtUtc: Hours(-1));
        await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None);
        var completedAt = CompletedAt(runId);

        var rejected = await RejectHandler().Handle(
            new RejectTaskCommand { TaskId = approvalId, TenantId = _tenantId, UserId = _userId, Comment = "late" },
            CancellationToken.None);

        Assert.False(rejected);
        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Expired, run.Status);
        Assert.Equal(completedAt, run.CompletedAtUtc);
        Assert.Equal(2, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == runId));
    }

    // ── 5. the pending list ───────────────────────────────────────────────

    [Fact]
    public async Task Pending_HidesAnOverdueApproval_EvenWhenTheSweeperNeverRan()
    {
        SeedParkedRun(expiresAtUtc: Hours(-1));

        var result = await PendingHandler().Handle(
            new GetPendingApprovalsQuery { TenantId = _tenantId, UserId = _userId }, CancellationToken.None);

        // Defence in depth: the list must never offer an action approve would refuse.
        Assert.Empty(result);
    }

    [Fact]
    public async Task Pending_ReturnsALiveApproval_AndCarriesItsDeadline()
    {
        var deadline = Hours(+3);
        SeedParkedRun(expiresAtUtc: deadline);

        var result = await PendingHandler().Handle(
            new GetPendingApprovalsQuery { TenantId = _tenantId, UserId = _userId }, CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal(GatedAction, item.ActionName);
        Assert.Equal(GatedPayload, item.Details);
        // Exposed so a client can show the remaining time; SQLite round-trips DateTime
        // through text, so compare at second resolution.
        Assert.Equal(deadline, item.ExpiresAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Pending_HidesASweptApproval()
    {
        SeedParkedRun(expiresAtUtc: Hours(-1));
        SeedParkedRun(expiresAtUtc: Hours(+1));
        await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None);

        var result = await PendingHandler().Handle(
            new GetPendingApprovalsQuery { TenantId = _tenantId, UserId = _userId }, CancellationToken.None);

        Assert.Single(result);
    }

    // ── 6. the boundaries the reconciler already promised ─────────────────

    [Fact]
    public async Task Sweep_OfAPlainChatApproval_ExpiresTheRow_ButWritesNoRunOrTranscriptRows()
    {
        // A chat approval has no AgentRun, so every reconciler entry point must no-op.
        var sessionId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        _db.ChatSessions.Add(new ChatSession { Id = sessionId, TenantId = _tenantId, UserId = _userId });
        _db.TaskApprovals.Add(NewApproval(approvalId, sessionId, Hours(-1)));
        _db.SaveChanges();

        Assert.Equal(1, await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        Assert.Equal(TaskApproval.ExpiredStatus,
            verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
        Assert.Empty(verify.AgentRunSteps.AsNoTracking().ToList());
        Assert.Empty(verify.ChatMessages.AsNoTracking().ToList());
    }

    [Fact]
    public async Task Sweep_NeverClosesARunBelongingToAnotherTenant()
    {
        // A foreign tenant's run parked on its own session...
        var (foreignRunId, foreignSessionId, _) = SeedParkedRun(Hours(+1), Guid.NewGuid(), Guid.NewGuid());
        // ...and an overdue approval of OURS pointing at that same session.
        var approvalId = Guid.NewGuid();
        _db.TaskApprovals.Add(NewApproval(approvalId, foreignSessionId, Hours(-1)));
        _db.SaveChanges();

        Assert.Equal(1, await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        Assert.Equal(TaskApproval.ExpiredStatus,
            verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
        // The reconciler's tenant/user predicate kept the other tenant's run parked.
        var foreign = verify.AgentRuns.AsNoTracking().Single(r => r.Id == foreignRunId);
        Assert.Equal(AgentRunStatuses.AwaitingApproval, foreign.Status);
        Assert.Null(foreign.CompletedAtUtc);
        Assert.Equal(1, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == foreignRunId));
    }

    // ── 7. batching and cancellation ──────────────────────────────────────

    [Fact]
    public async Task Sweep_DrainsABacklogLargerThanOneBatch()
    {
        for (var i = 0; i < 7; i++) SeedChatApproval(Hours(-1));

        var options = new ApprovalExpiryOptions { BatchSize = 2 };
        Assert.Equal(7, await Sweeper(options).SweepAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        Assert.Equal(0, verify.TaskApprovals.AsNoTracking().Count(t => t.Status == "Pending"));
    }

    [Fact]
    public async Task Sweep_StopsAtTheBatchCap_AndLeavesTheRestForTheNextTick()
    {
        for (var i = 0; i < 7; i++) SeedChatApproval(Hours(-1));

        // One batch of two per pass: a huge backlog can never monopolise one sweep.
        var options = new ApprovalExpiryOptions { BatchSize = 2, MaxBatchesPerSweep = 1 };
        Assert.Equal(2, await Sweeper(options).SweepAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        Assert.Equal(5, verify.TaskApprovals.AsNoTracking().Count(t => t.Status == "Pending"));
    }

    [Fact]
    public async Task Sweep_ObservesCancellation()
    {
        SeedChatApproval(Hours(-1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sweeper().SweepAsync(DateTime.UtcNow, cancelled.Token));

        using var verify = NewContext();
        Assert.Equal(1, verify.TaskApprovals.AsNoTracking().Count(t => t.Status == "Pending"));
    }

    // ── 8. code default vs configuration ──────────────────────────────────

    [Fact]
    public void OptionDefaults_MatchTheEntitysOwnFallback()
    {
        // A creation site that never consults configuration and one that does must
        // produce the same window, or the fix is silently inert on one of the two paths.
        var options = new ApprovalExpiryOptions();
        Assert.Equal(TaskApproval.DefaultTimeToLiveHours, options.TimeToLiveHours);
        Assert.Equal(TimeSpan.FromHours(TaskApproval.DefaultTimeToLiveHours), options.TimeToLive);
        Assert.True(options.Enabled);

        // The entity's own default must be a real, future deadline — never DateTime
        // default, which would make every row born expired.
        var fresh = new TaskApproval();
        Assert.Equal(
            fresh.CreatedAtUtc.AddHours(TaskApproval.DefaultTimeToLiveHours),
            fresh.ExpiresAtUtc,
            TimeSpan.FromSeconds(1));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static DateTime Hours(double delta) => DateTime.UtcNow.AddHours(delta);

    private ApproveTaskCommand Approve(Guid approvalId)
        => new() { TaskId = approvalId, TenantId = _tenantId, UserId = _userId };

    private ApproveTaskCommandHandler ApproveHandler()
        => new(NewContext(), _orchestrator, _protector, NullLogger<ApproveTaskCommandHandler>.Instance);

    private RejectTaskCommandHandler RejectHandler()
        => new(NewContext(), _protector, NullLogger<RejectTaskCommandHandler>.Instance);

    private GetPendingApprovalsQueryHandler PendingHandler()
        => new(NewContext(), _protector);

    private ApprovalExpirySweeper Sweeper(ApprovalExpiryOptions? options = null)
        => new(NewContext(), _protector, Options.Create(options ?? new ApprovalExpiryOptions()),
            NullLogger<ApprovalExpirySweeper>.Instance);

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private DateTime? CompletedAt(Guid runId)
    {
        using var db = NewContext();
        return db.AgentRuns.AsNoTracking().Single(r => r.Id == runId).CompletedAtUtc;
    }

    private User NewUser(Guid tenantId, Guid userId) => new()
    {
        Id = userId,
        TenantId = tenantId,
        Username = $"u{userId:N}",
        Email = $"{userId:N}@t.test",
        PasswordHash = "x",
        Role = "User"
    };

    private TaskApproval NewApproval(Guid approvalId, Guid sessionId, DateTime expiresAtUtc) => new()
    {
        Id = approvalId,
        TenantId = _tenantId,
        UserId = _userId,
        ChatSessionId = sessionId,
        ActionName = GatedAction,
        ParametersJson = _protector.Protect(GatedPayload),
        Status = "Pending",
        ExpiresAtUtc = expiresAtUtc
    };

    /// <summary>An approval with no agent run behind it — the plain chat shape.</summary>
    private Guid SeedChatApproval(DateTime expiresAtUtc)
    {
        var sessionId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        _db.ChatSessions.Add(new ChatSession { Id = sessionId, TenantId = _tenantId, UserId = _userId });
        _db.TaskApprovals.Add(NewApproval(approvalId, sessionId, expiresAtUtc));
        _db.SaveChanges();
        return approvalId;
    }

    /// <summary>
    /// The stuck state exactly as StreamAgentRunCommandHandler leaves it: a hidden
    /// IsAgentRun session, a run at AwaitingApproval with a null CompletedAtUtc, the gated
    /// attempt already captured as step 1, and a Pending approval on that session — with
    /// the deadline the test wants.
    /// </summary>
    private (Guid RunId, Guid SessionId, Guid ApprovalId) SeedParkedRun(
        DateTime expiresAtUtc, Guid? tenantId = null, Guid? userId = null)
    {
        var runTenantId = tenantId ?? _tenantId;
        var runUserId = userId ?? _userId;
        if (runTenantId != _tenantId)
        {
            _db.Tenants.Add(new Tenant { Id = runTenantId, Name = "Other" });
            _db.Users.Add(NewUser(runTenantId, runUserId));
        }

        var session = new ChatSession
        {
            TenantId = runTenantId,
            UserId = runUserId,
            Title = "Mục tiêu",
            IsAgentRun = true
        };
        var approvalId = Guid.NewGuid();
        var run = new AgentRun
        {
            TenantId = runTenantId,
            UserId = runUserId,
            ChatSessionId = session.Id,
            Goal = "Mục tiêu",
            Status = AgentRunStatuses.AwaitingApproval,
            CompletedAtUtc = null
        };
        run.Steps.Add(new AgentRunStep
        {
            Ordinal = 1,
            Action = GatedAction,
            Input = GatedPayload,
            Observation = $"[HITL_APPROVAL_REQUIRED:{approvalId}]"
        });

        _db.ChatSessions.Add(session);
        _db.AgentRuns.Add(run);
        if (runTenantId == _tenantId && runUserId == _userId)
            _db.TaskApprovals.Add(NewApproval(approvalId, session.Id, expiresAtUtc));
        _db.SaveChanges();

        return (run.Id, session.Id, approvalId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Model-free tool boundary: counts executions so an expired approval that
    /// nonetheless ran would be impossible to miss.</summary>
    private sealed class RecordingOrchestrator : IAgentOrchestrator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string Output { get; set; } = "tool output";

        public IAsyncEnumerable<string> StreamProcessQueryAsync(
            Guid tenantId, Guid sessionId, Guid userId, string userRole, string query,
            LMKit.TextGeneration.Chat.ChatHistory history, AgentRequestOptions? options,
            CancellationToken cancellationToken, IList<AgentRunStepData>? stepSink = null)
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
