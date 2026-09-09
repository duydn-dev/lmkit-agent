using System.Runtime.CompilerServices;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.Approvals;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Application.Approvals.Handlers;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using LmKitOmniApi.Services;
using LMKit.TextGeneration.Chat;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// T3 — a run that trips a human-in-the-loop gate now RESUMES its ReAct loop once the
/// human approves, instead of stopping at that one tool call.
///
/// <para>What made that hard is durability, not plumbing: the orchestrator's ReAct pass
/// is an in-process <c>await foreach</c> whose state dies with the request, while an
/// approval can land hours later on another replica. So these tests exercise the
/// continuation the way production reaches it — through the database only. Every test
/// approves through the real handler, then drives <see cref="AgentRunResumeService"/>
/// from a FRESH <see cref="HermesDbContext"/>, exactly as a background worker on a
/// different process would; nothing is carried across in memory.</para>
///
/// <para>Before this change every "resumes" assertion below failed with
/// <c>CompletedAfterApproval</c>: the run reached a terminal state and no continuation
/// existed to drive. The truthful-stop assertions passed then and still pass now — that
/// behaviour is preserved for exactly the cases where a continuation is impossible.</para>
/// </summary>
public sealed class AgentRunResumeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly TaskApprovalPayloadProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly ResumingOrchestrator _orchestrator = new();

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private const string GatedAction = "DBWRITE";
    private const string GatedPayload = "UPDATE customers SET tier = 'gold' WHERE id = 1";
    private const string ApprovedOutput = "1 row updated";

    public AgentRunResumeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = NewContext();
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T" });
        _db.Users.Add(new User
        {
            Id = _userId,
            TenantId = _tenantId,
            Username = "u",
            Email = "u@t.test",
            PasswordHash = "x",
            Role = "User"
        });
        _db.SaveChanges();
    }

    // ── 1. approve queues a continuation instead of ending the run ─────────

    [Fact]
    public async Task Approve_HandsTheRunBackToTheReActLoop_InsteadOfEndingIt()
    {
        var (runId, _, approvalId) = SeedParkedRun();

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);
        Assert.Equal(ApproveTaskOutcome.Completed, outcome.Outcome);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);

        // FAILS before the fix: the run reached CompletedAfterApproval with a completion
        // time and nothing ever planned again.
        Assert.Equal(AgentRunStatuses.Running, run.Status);
        Assert.Equal(AgentRunResumeStates.Pending, run.ResumeState);
        Assert.Null(run.CompletedAtUtc);
        // The claim, not the queueing, is what spends budget.
        Assert.Equal(0, run.ResumeCount);

        // The approved call is still recorded exactly as before — resuming did not cost
        // the timeline the execution record it always had.
        var steps = verify.AgentRunSteps.AsNoTracking()
            .Where(s => s.AgentRunId == runId).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(ApprovedOutput, steps[1].Observation);
    }

    [Fact]
    public async Task Resume_ReplaysTheApprovedObservation_AndProducesFurtherSteps()
    {
        var (runId, _, approvalId) = SeedParkedRun();
        _orchestrator.Script = ResumeScript.Answer("Đã nâng hạng khách hàng.", ("RAG", "chính sách hạng", "hạng vàng: 10%"));

        await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);
        var resumed = await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(1, resumed);

        // The continuation was seeded with the goal AND the approved observation — this
        // is the "fed back in" half of the fix, and it is reconstructed from rows only.
        var query = Assert.Single(_orchestrator.Queries);
        Assert.Contains("Mục tiêu", query, StringComparison.Ordinal);
        Assert.Contains(ApprovedOutput, query, StringComparison.Ordinal);
        Assert.Contains(GatedPayload, query, StringComparison.Ordinal);
        // The gated ATTEMPT's marker is internal protocol, never replayed as data.
        Assert.DoesNotContain("[HITL_APPROVAL_REQUIRED:", query, StringComparison.Ordinal);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);

        Assert.Equal(AgentRunStatuses.Completed, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Null(run.ResumeState);
        Assert.Null(run.ResumeLeaseUntilUtc);
        Assert.Equal(1, run.ResumeCount);
        Assert.Contains("Đã nâng hạng khách hàng.", run.Result);
        // …and the approved tool output is still readable in the same result.
        Assert.Contains(ApprovedOutput, run.Result!);

        // A further step: the run kept planning rather than ending on the approved call.
        var steps = verify.AgentRunSteps.AsNoTracking()
            .Where(s => s.AgentRunId == runId).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(3, steps.Count);
        Assert.Equal(3, steps[2].Ordinal);
        Assert.Equal("RAG", steps[2].Action);
        Assert.Equal("hạng vàng: 10%", steps[2].Observation);

        // The step/gate markers are stripped out of the stored answer by exactly the
        // regex the first pass uses — one shared implementation, so a resumed run's
        // result can never be shaped differently from a first-pass one.
        Assert.DoesNotContain("[STEP:", run.Result!, StringComparison.Ordinal);
        Assert.DoesNotContain("[HITL_APPROVAL_REQUIRED:", run.Result!, StringComparison.Ordinal);
    }

    // ── 2. a resumed run may gate again ────────────────────────────────────

    [Fact]
    public async Task Resume_ThatHitsASecondGate_ParksTheRunAgain_AndCanBeApprovedIntoAnotherPass()
    {
        var (runId, sessionId, approvalId) = SeedParkedRun();
        var secondApprovalId = Guid.NewGuid();
        _orchestrator.Script = ResumeScript.Gate(secondApprovalId, "DBWRITE", "DELETE FROM audit");

        await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);
        await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None);

        // The second gate is a real approval row (the fake orchestrator writes one the
        // way the real one does) plus the parked run.
        _db.TaskApprovals.Add(NewApproval(secondApprovalId, sessionId));
        _db.SaveChanges();

        using (var afterGate = NewContext())
        {
            var parked = afterGate.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
            Assert.Equal(AgentRunStatuses.AwaitingApproval, parked.Status);
            Assert.Null(parked.CompletedAtUtc);
            // Nothing is queued while a human is being waited on: a gate is not work.
            Assert.Null(parked.ResumeState);
            Assert.Equal(1, parked.ResumeCount);

            // The marker step is persisted, which is how the agent-run page re-adopts the
            // gate after a reload and how the reconciler finds the run again.
            var last = afterGate.AgentRunSteps.AsNoTracking()
                .Where(s => s.AgentRunId == runId).OrderBy(s => s.Ordinal).ToList()[^1];
            Assert.Contains(secondApprovalId.ToString(), last.Observation, StringComparison.OrdinalIgnoreCase);
        }

        // Approving the SECOND gate buys a second continuation — the loop is re-entrant.
        _orchestrator.Script = ResumeScript.Answer("Xong.");
        await ApproveHandler().Handle(Approve(secondApprovalId), CancellationToken.None);

        using (var afterSecondApprove = NewContext())
        {
            var queued = afterSecondApprove.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
            Assert.Equal(AgentRunStatuses.Running, queued.Status);
            Assert.Equal(AgentRunResumeStates.Pending, queued.ResumeState);
        }

        Assert.Equal(1, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Completed, run.Status);
        Assert.Equal(2, run.ResumeCount);
    }

    // ── 2b. capacity is not failure ────────────────────────────────────────

    [Fact]
    public async Task Resume_RefusedByTheInferenceQueue_IsRequeued_NotFailed()
    {
        // The single chat permit stayed busy past the queue's bound. That is the deployment's
        // capacity, not the run's doing. Before this fix the generic catch turned it into a
        // permanent Failed -- and the worse variant, the orchestrator ENDING the stream with a
        // warning line instead of throwing, turned it into Completed with the overload notice
        // stored as the run's answer.
        var (runId, _, approvalId) = SeedParkedRun();
        _orchestrator.Script = ResumeScript.RefusedByQueue();

        await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        // Zero resumed, and the sweep ENDS on the refusal. The first version of this fix
        // requeued the run as Pending and let the same sweep pick it straight back up: three
        // refusals in one pass, the whole resume budget gone in under a second.
        Assert.Equal(0, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));

        using (var afterRefusal = NewContext())
        {
            var run = afterRefusal.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
            Assert.Equal(AgentRunStatuses.Running, run.Status);
            Assert.Equal(AgentRunResumeStates.Pending, run.ResumeState);
            Assert.Null(run.CompletedAtUtc);
            Assert.Null(run.Error);
            Assert.Equal(1, run.ResumeCount);

            // A refusal is not prior progress. Persisting it would replay
            // "admission_refused: ..." into the next attempt's prompt as if the agent did it.
            Assert.DoesNotContain(
                afterRefusal.AgentRunSteps.AsNoTracking().Where(s => s.AgentRunId == runId).ToList(),
                s => s.Action == AgentRunStepData.AdmissionRefusedAction);
        }

        // Capacity came back: the same queued run completes on the next pass, and the budget
        // it spent on the refused attempt is the ONLY thing that bounds sustained overload.
        _orchestrator.Script = ResumeScript.Answer("Xong.");
        Assert.Equal(1, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        var completed = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Completed, completed.Status);
        Assert.Equal(2, completed.ResumeCount);
    }

    // ── 3. resolving twice never double-steps ──────────────────────────────

    [Fact]
    public async Task ApprovingTwice_QueuesExactlyOneContinuation_AndResumesOnce()
    {
        var (runId, _, approvalId) = SeedParkedRun();
        _orchestrator.Script = ResumeScript.Answer("Xong.", ("RAG", "q", "o"));

        var first = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);
        var second = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, first.Outcome);
        Assert.Equal(ApproveTaskOutcome.Conflict, second.Outcome);
        Assert.Equal(1, _orchestrator.DirectCalls);

        await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None);
        // A second pass finds nothing left to claim.
        Assert.Equal(0, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(1, run.ResumeCount);
        Assert.Equal(1, _orchestrator.Streams);
        // gated attempt + approved execution + exactly one resumed step
        Assert.Equal(3, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == runId));
    }

    [Fact]
    public async Task TwoWorkersThatBothSawTheSameQueuedRun_ProduceExactlyOneClaim()
    {
        var (runId, _, approvalId) = SeedParkedRun();
        _orchestrator.Script = ResumeScript.Answer("Xong.", ("RAG", "q", "o"));
        await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        // The situation two replicas are actually in: both selected the run while it read
        // (Pending, ResumeCount = 0), and both now try to take it. Driven head-on rather
        // than through threads — a wall-clock race would prove the same thing only when
        // the timing happened to line up, and would be testing SQLite's tolerance for
        // concurrent contexts on one connection rather than this predicate.
        var now = DateTime.UtcNow;
        var first = await ResumeService().TryClaimAsync(runId, observedResumeCount: 0, now, CancellationToken.None);
        var second = await ResumeService().TryClaimAsync(runId, observedResumeCount: 0, now, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);

        using (var afterClaim = NewContext())
        {
            var claimed = afterClaim.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
            Assert.Equal(AgentRunResumeStates.Claimed, claimed.ResumeState);
            // One claim, one unit of budget — never two.
            Assert.Equal(1, claimed.ResumeCount);
        }

        // And the loser cannot get in by simply polling again: the winner's lease is live.
        Assert.Equal(0, await ResumeService().ResumePendingAsync(now, CancellationToken.None));
        Assert.Equal(0, _orchestrator.Streams);
    }

    // ── 4. the decisions that must NOT resume ──────────────────────────────

    [Fact]
    public async Task Reject_StillReachesTheTruthfulTerminalState_AndQueuesNothing()
    {
        var (runId, _, approvalId) = SeedParkedRun();

        var rejected = await RejectHandler().Handle(
            new RejectTaskCommand { TaskId = approvalId, TenantId = _tenantId, UserId = _userId, Comment = "Quá rủi ro" },
            CancellationToken.None);

        Assert.True(rejected);
        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Rejected, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Null(run.ResumeState);
        Assert.Equal(0, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Expiry_StillReachesTheTruthfulTerminalState_AndQueuesNothing()
    {
        var (runId, _, approvalId) = SeedParkedRun(expiresAtUtc: DateTime.UtcNow.AddHours(-1));

        var expired = await Sweeper().SweepAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(1, expired);
        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Expired, run.Status);
        Assert.Null(run.ResumeState);
        Assert.Equal(approvalId, verify.TaskApprovals.AsNoTracking().Single().Id);
        Assert.Equal(0, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Approve_WhenTheToolThrows_StillFailsTheRun_AndQueuesNothing()
    {
        var (runId, _, approvalId) = SeedParkedRun();
        _orchestrator.ThrowOnDirect = true;

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Failed, outcome.Outcome);
        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Failed, run.Status);
        Assert.Null(run.ResumeState);
    }

    [Fact]
    public async Task Approve_InAHostWithNoResumeWorker_KeepsTheOldTruthfulStop()
    {
        var (runId, _, approvalId) = SeedParkedRun();

        // Exactly how the handler resolves when AddAgentRunResume was never called.
        var handler = new ApproveTaskCommandHandler(
            NewContext(), _orchestrator, _protector, NullLogger<ApproveTaskCommandHandler>.Instance,
            resumeQueue: null);
        await handler.Handle(Approve(approvalId), CancellationToken.None);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Null(run.ResumeState);
        Assert.Contains(ApprovedOutput, run.Result!);
    }

    [Fact]
    public void ApproveHandler_ResolvesFromDiWithoutAResumeQueueRegistered()
    {
        // The optional constructor parameter is load-bearing: if Microsoft DI could not
        // fill it with its default, a host that never called AddAgentRunResume would fail
        // to construct the handler at all and every approval would 500.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_protector);
        services.AddSingleton<IAgentOrchestrator>(_orchestrator);
        services.AddDbContext<HermesDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<ApproveTaskCommandHandler>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ApproveTaskCommandHandler>());
    }

    // ── 5. the bounds ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResumeBudget_Exhausted_ClosesTheRunTruthfullyAndSaysWhy()
    {
        var (runId, _, approvalId) = SeedParkedRun(resumeCount: 2);

        await ApproveHandler(Options(maxResumes: 2)).Handle(Approve(approvalId), CancellationToken.None);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Null(run.ResumeState);
        Assert.Contains("giới hạn", run.Result!);
        Assert.Equal(0, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task StepCap_Reached_ClosesTheRunTruthfullyInsteadOfResuming()
    {
        var (runId, _, approvalId) = SeedParkedRun();

        // One gated attempt + the approved execution = 2 steps, which is the cap.
        await ApproveHandler(Options(maxTotalSteps: 2)).Handle(Approve(approvalId), CancellationToken.None);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        Assert.Contains("bước công cụ", run.Result!);
        Assert.Null(run.ResumeState);
    }

    [Fact]
    public async Task AQueuedRunThatIsAlreadyOverBudget_IsClosedByTheWorker_NotReselectedForever()
    {
        // The state a shutdown mid-resume can leave behind: re-queued with the budget
        // already spent. Without the worker's own bound check this run would be selected
        // on every poll for the rest of the process's life and never claimed.
        var (runId, _, _) = SeedParkedRun(resumeCount: 3, resumeState: AgentRunResumeStates.Pending);

        var resumed = await ResumeService(Options(maxResumes: 3)).ResumePendingAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(0, resumed);
        Assert.Equal(0, _orchestrator.Streams);
        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        Assert.Null(run.ResumeState);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task Drain_WhenResumingIsDisabled_ClosesEverythingQueuedTruthfully()
    {
        var (runId, _, _) = SeedParkedRun(resumeState: AgentRunResumeStates.Pending);

        var closed = await ResumeService(Options(enabled: false)).DrainAsync(CancellationToken.None);

        Assert.Equal(1, closed);
        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        Assert.Null(run.ResumeState);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Contains("đang tắt", run.Result!);
    }

    // ── 6. crash + shutdown ────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_MidResume_ReleasesTheModelLease_AndRequeuesTheRun()
    {
        var (runId, _, approvalId) = SeedParkedRun();
        _orchestrator.Script = ResumeScript.BlockForever(("RAG", "q", "o"));

        await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        using var shutdown = new CancellationTokenSource();
        var pass = Task.Run(() => ResumeService().ResumePendingAsync(DateTime.UtcNow, shutdown.Token));
        Assert.True(await _orchestrator.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _orchestrator.Lease.CurrentCount); // the pass holds it

        await shutdown.CancelAsync();
        await pass;

        // The single-permit lease the real orchestrator takes is released on the
        // cancellation path, because the worker disposes the enumerator instead of
        // abandoning it.
        Assert.Equal(1, _orchestrator.Lease.CurrentCount);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        // Re-queued, not failed: a shutdown is not an outcome. Budget was still spent by
        // the claim, so this cannot spin.
        Assert.Equal(AgentRunStatuses.Running, run.Status);
        Assert.Equal(AgentRunResumeStates.Pending, run.ResumeState);
        Assert.Null(run.ResumeLeaseUntilUtc);
        Assert.Null(run.CompletedAtUtc);
        Assert.Equal(1, run.ResumeCount);
        // Steps that really ran before the cut are kept, and replay next time.
        Assert.Equal(3, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == runId));
    }

    [Fact]
    public async Task AClaimWhoseLeaseLapsed_IsRetakenByTheNextWorker()
    {
        // What a process that died mid-pass leaves behind.
        var (runId, _, _) = SeedParkedRun(
            resumeState: AgentRunResumeStates.Claimed,
            resumeLeaseUntilUtc: DateTime.UtcNow.AddMinutes(-30));
        _orchestrator.Script = ResumeScript.Answer("Tiếp tục sau sự cố.");

        Assert.Equal(1, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Completed, run.Status);
        Assert.Null(run.ResumeState);
        Assert.Equal(1, run.ResumeCount);
    }

    [Fact]
    public async Task AClaimStillWithinItsLease_IsLeftAlone()
    {
        SeedParkedRun(
            resumeState: AgentRunResumeStates.Claimed,
            resumeLeaseUntilUtc: DateTime.UtcNow.AddMinutes(30));

        Assert.Equal(0, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.Equal(0, _orchestrator.Streams);
    }

    // ── 7. authority is re-checked at continuation time ────────────────────

    [Fact]
    public async Task Resume_ForAUserWhoIsNoLongerActive_FailsTheRunInsteadOfPlanning()
    {
        var (runId, _, approvalId) = SeedParkedRun();
        await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        var user = _db.Users.Single(u => u.Id == _userId);
        user.IsActive = false;
        _db.SaveChanges();

        await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(0, _orchestrator.Streams);
        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Failed, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Null(run.ResumeState);
        Assert.Contains("không còn hoạt động", run.Error!);
    }

    // ── 8. chat is untouched ───────────────────────────────────────────────

    [Fact]
    public async Task Approve_ForAnOrdinaryChatApproval_QueuesNothingAndWritesNoRunRows()
    {
        var sessionId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        _db.ChatSessions.Add(new ChatSession { Id = sessionId, TenantId = _tenantId, UserId = _userId });
        _db.TaskApprovals.Add(NewApproval(approvalId, sessionId));
        _db.SaveChanges();

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, outcome.Outcome);
        Assert.Equal(ApprovedOutput, outcome.Result);
        using var verify = NewContext();
        Assert.Empty(verify.AgentRuns.AsNoTracking().ToList());
        Assert.Empty(verify.AgentRunSteps.AsNoTracking().ToList());
        Assert.Empty(verify.ChatMessages.AsNoTracking().ToList());
        Assert.Equal(0, await ResumeService().ResumePendingAsync(DateTime.UtcNow, CancellationToken.None));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static AgentRunResumeOptions Options(
        bool enabled = true, int maxResumes = 3, int maxTotalSteps = 24)
        => new()
        {
            Enabled = enabled,
            MaxResumesPerRun = maxResumes,
            MaxTotalSteps = maxTotalSteps,
            LeaseSeconds = 900
        };

    private ApproveTaskCommand Approve(Guid approvalId)
        => new() { TaskId = approvalId, TenantId = _tenantId, UserId = _userId };

    private ApproveTaskCommandHandler ApproveHandler(AgentRunResumeOptions? options = null)
        => new(NewContext(), _orchestrator, _protector, NullLogger<ApproveTaskCommandHandler>.Instance,
            new AgentRunResumeQueue(Microsoft.Extensions.Options.Options.Create(options ?? Options())));

    private RejectTaskCommandHandler RejectHandler()
        => new(NewContext(), _protector, NullLogger<RejectTaskCommandHandler>.Instance);

    private ApprovalExpirySweeper Sweeper()
        => new(NewContext(), _protector,
            Microsoft.Extensions.Options.Options.Create(new ApprovalExpiryOptions()),
            NullLogger<ApprovalExpirySweeper>.Instance);

    /// <summary>
    /// A continuation driver on a FRESH DbContext — the point being that it shares
    /// nothing with the approve request but the database, exactly like a worker in
    /// another process.
    /// </summary>
    private AgentRunResumeService ResumeService(AgentRunResumeOptions? options = null)
        => new(NewContext(), _orchestrator, new StubHistoryFactory(),
            new AgentRunResumeQueue(Microsoft.Extensions.Options.Options.Create(options ?? Options())),
            NullLogger<AgentRunResumeService>.Instance);

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private TaskApproval NewApproval(Guid approvalId, Guid sessionId, DateTime? expiresAtUtc = null) => new()
    {
        Id = approvalId,
        TenantId = _tenantId,
        UserId = _userId,
        ChatSessionId = sessionId,
        ActionName = GatedAction,
        ParametersJson = _protector.Protect(GatedPayload),
        Status = "Pending",
        ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddHours(1)
    };

    /// <summary>
    /// The stuck state exactly as <c>StreamAgentRunCommandHandler</c> leaves it: a hidden
    /// IsAgentRun session, a run at AwaitingApproval with a null CompletedAtUtc, the
    /// gated attempt already captured as step 1 (observation = the marker), and a Pending
    /// approval on that session. The resume fields default to "never resumed" and can be
    /// overridden to stage a crash or an exhausted budget.
    /// </summary>
    private (Guid RunId, Guid SessionId, Guid ApprovalId) SeedParkedRun(
        DateTime? expiresAtUtc = null,
        int resumeCount = 0,
        string? resumeState = null,
        DateTime? resumeLeaseUntilUtc = null)
    {
        var session = new ChatSession
        {
            TenantId = _tenantId,
            UserId = _userId,
            Title = "Mục tiêu",
            IsAgentRun = true
        };
        var approvalId = Guid.NewGuid();
        var run = new AgentRun
        {
            TenantId = _tenantId,
            UserId = _userId,
            ChatSessionId = session.Id,
            Goal = "Mục tiêu: nâng hạng khách hàng VIP",
            // A run staged mid-continuation is Running, exactly as the reconciler leaves it.
            Status = resumeState is null ? AgentRunStatuses.AwaitingApproval : AgentRunStatuses.Running,
            CompletedAtUtc = null,
            ResumeCount = resumeCount,
            ResumeState = resumeState,
            ResumeLeaseUntilUtc = resumeLeaseUntilUtc
        };
        run.Steps.Add(new AgentRunStep
        {
            Ordinal = 1,
            Action = GatedAction,
            Input = GatedPayload,
            Observation = $"[HITL_APPROVAL_REQUIRED:{approvalId}]"
        });
        if (resumeState is not null)
        {
            // A queued run already carries the approved observation as its last step.
            run.Steps.Add(new AgentRunStep
            {
                Ordinal = 2,
                Action = GatedAction,
                Input = GatedPayload,
                Observation = ApprovedOutput
            });
        }

        _db.ChatSessions.Add(session);
        _db.AgentRuns.Add(run);
        _db.TaskApprovals.Add(NewApproval(approvalId, session.Id, expiresAtUtc));
        _db.SaveChanges();

        return (run.Id, session.Id, approvalId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>The chat history seam, model-free (ChatHistory tolerates a null model).</summary>
    private sealed class StubHistoryFactory : IAgentRunHistoryFactory
    {
        public Task<ChatHistory> CreateAsync(CancellationToken ct) => Task.FromResult(new ChatHistory(null!));
    }

    /// <summary>What the faked orchestrator does on a continuation pass.</summary>
    private sealed record ResumeScript(
        IReadOnlyList<(string Action, string Input, string Observation)> Steps,
        string? FinalAnswer,
        Guid? GateApprovalId,
        bool Block)
    {
        public static ResumeScript Answer(string answer, params (string, string, string)[] steps)
            => new(steps, answer, null, false);

        public static ResumeScript Gate(Guid approvalId, string action, string input)
            => new([], null, approvalId, false) { GateAction = action, GateInput = input };

        public static ResumeScript BlockForever(params (string, string, string)[] steps)
            => new(steps, null, null, true);

        public string GateAction { get; init; } = string.Empty;
        public string GateInput { get; init; } = string.Empty;

        /// <summary>The inference queue turned the pass away: capacity, not failure.</summary>
        public bool RefuseAdmission { get; init; }

        public static ResumeScript RefusedByQueue() => new([], null, null, false) { RefuseAdmission = true };
    }

    /// <summary>
    /// Model-free stand-in for the real orchestrator at both seams it is used through
    /// here. The streaming side mirrors the structure that actually matters for this
    /// change: the single-permit inference lease is taken with <c>await using</c> INSIDE
    /// the iterator, so it is released only if the consumer disposes the enumerator —
    /// which is precisely what the cancellation test asserts.
    /// </summary>
    private sealed class ResumingOrchestrator : IAgentOrchestrator
    {
        private int _directCalls;
        private int _streams;

        public const string RefusalMessage = "He thong dang ban, vui long thu lai sau.";
        public SemaphoreSlim Lease { get; } = new(1, 1);
        public TaskCompletionSource<bool> StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Queries { get; } = [];
        public ResumeScript Script { get; set; } = ResumeScript.Answer("Xong.");
        public bool ThrowOnDirect { get; set; }
        public int DirectCalls => Volatile.Read(ref _directCalls);
        public int Streams => Volatile.Read(ref _streams);

        public Task<string> ExecuteDirectActionAsync(
            Guid tenantId, Guid userId, string action, string query,
            Guid? approvalId = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _directCalls);
            if (ThrowOnDirect) throw new InvalidOperationException("tool blew up");
            return Task.FromResult(ApprovedOutput);
        }

        public async IAsyncEnumerable<string> StreamProcessQueryAsync(
            Guid tenantId, Guid sessionId, Guid userId, string userRole, string query,
            ChatHistory history, AgentRequestOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            IList<AgentRunStepData>? stepSink = null)
        {
            Interlocked.Increment(ref _streams);
            lock (Queries) Queries.Add(query);

            await Lease.WaitAsync(cancellationToken);
            try
            {
                StreamStarted.TrySetResult(true);

                if (Script.RefuseAdmission)
                {
                    // Exactly the real orchestrator's shape for a sink caller: a timeline step,
                    // a visible status line, then the exception -- never a warning-as-answer.
                    stepSink?.Add(new AgentRunStepData(AgentRunStepData.AdmissionRefusedAction, "chat", RefusalMessage));
                    yield return "[THINKING]: " + RefusalMessage + "\n";
                    throw new InferenceQueueRejectedException(
                        "chat", InferenceQueueRejectionReason.WaitTimeout, TimeSpan.FromSeconds(1), 1, RefusalMessage);
                }

                yield return "[THINKING]: tiếp tục\n";

                foreach (var (action, input, observation) in Script.Steps)
                {
                    stepSink?.Add(new AgentRunStepData(action, input, observation));
                    yield return $"[STEP:{{\"action\":\"{action}\"}}]";
                }

                if (Script.GateApprovalId is Guid gateId)
                {
                    stepSink?.Add(new AgentRunStepData(
                        Script.GateAction, Script.GateInput, $"[HITL_APPROVAL_REQUIRED:{gateId}]"));
                    yield return $"[HITL_APPROVAL_REQUIRED:{gateId}]";
                    yield break;
                }

                if (Script.Block)
                {
                    // Never completes on its own: only cancellation ends this pass, which
                    // is what a shutdown mid-inference looks like.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                if (Script.FinalAnswer is { } answer) yield return answer;
            }
            finally
            {
                Lease.Release();
            }
        }
    }
}
