using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Application.Approvals.Handlers;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// T7 — the agent-run / approval lifecycle. A run that trips a human-in-the-loop
/// gate is finalized as "AwaitingApproval" with a null CompletedAtUtc, and before
/// this fix NOTHING ever wrote AgentRun.Status again: approving or rejecting
/// resolved only the task_approvals row, so the run hung in that state forever, its
/// step timeline never showed the call that actually executed, and the tool output
/// existed only in the approve endpoint's HTTP response.
///
/// These tests pin the resolution contract end to end over a real (SQLite) DB with
/// the model boundary faked: approve and reject both move the run to a TERMINAL
/// status with CompletedAtUtc set, both append a step, the approved output is
/// persisted where the user can read it, a second approve of the same id can never
/// execute the tool twice, and a plain chat approval is left completely alone.
/// </summary>
public sealed class AgentRunApprovalLifecycleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly TaskApprovalPayloadProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly RecordingOrchestrator _orchestrator = new();

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private const string GatedAction = "DBWRITE";
    private const string GatedPayload = "DELETE FROM customers WHERE id = 1";

    public AgentRunApprovalLifecycleTests()
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

    // ── approve ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_LeavesAwaitingApproval_RecordsTheApprovedCallAsAStep_AndPersistsTheResult()
    {
        var (runId, sessionId, approvalId) = SeedParkedRun();
        _orchestrator.Output = "1 row deleted";

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, outcome.Outcome);
        Assert.Equal("1 row deleted", outcome.Result);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);

        // 1. The run left AwaitingApproval for a terminal state, with a completion time.
        Assert.NotEqual(AgentRunStatuses.AwaitingApproval, run.Status);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        Assert.NotNull(run.CompletedAtUtc);

        // 2. The approved call is in the step history, after the gated attempt the
        //    orchestrator already recorded, with the real tool output as observation.
        var steps = verify.AgentRunSteps.AsNoTracking()
            .Where(s => s.AgentRunId == runId).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(2, steps[1].Ordinal);
        Assert.Equal(GatedAction, steps[1].Action);
        Assert.Equal(GatedPayload, steps[1].Input);
        Assert.Equal("1 row deleted", steps[1].Observation);

        // 3. The output is readable from the run itself and from its session transcript.
        Assert.Contains("1 row deleted", run.Result);
        var assistant = Assert.Single(verify.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == sessionId && m.Role == "assistant").ToList());
        Assert.Contains("1 row deleted", assistant.Content);

        // The approval row itself is resolved.
        Assert.Equal("Completed", verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
    }

    [Fact]
    public async Task Approve_WhenTheToolThrows_StillMovesTheRunToATerminalFailedState()
    {
        var (runId, _, approvalId) = SeedParkedRun();
        _orchestrator.Throw = true;

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Failed, outcome.Outcome);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Failed, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(run.Error));
        // The user-safe error never carries the exception text.
        Assert.DoesNotContain(RecordingOrchestrator.FailureMessage, run.Error!);
        Assert.Equal(2, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == runId));
        Assert.Equal("Failed", verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
    }

    // ── reject ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_MovesTheRunToATerminalRejectedState_AndRecordsTheRefusal()
    {
        var (runId, sessionId, approvalId) = SeedParkedRun();

        var rejected = await RejectHandler().Handle(
            new RejectTaskCommand
            {
                TaskId = approvalId,
                TenantId = _tenantId,
                UserId = _userId,
                Comment = "Quá rủi ro"
            },
            CancellationToken.None);

        Assert.True(rejected);
        Assert.Equal(0, _orchestrator.Calls);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.Rejected, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Contains("Quá rủi ro", run.Error);
        // Nothing executed, so the run has no result.
        Assert.Null(run.Result);

        var steps = verify.AgentRunSteps.AsNoTracking()
            .Where(s => s.AgentRunId == runId).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(GatedAction, steps[1].Action);
        Assert.Equal(GatedPayload, steps[1].Input);
        Assert.Contains("từ chối", steps[1].Observation);

        Assert.Single(verify.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == sessionId && m.Role == "assistant").ToList());
        Assert.Equal("Rejected", verify.TaskApprovals.AsNoTracking().Single(t => t.Id == approvalId).Status);
    }

    [Fact]
    public async Task Reject_NeverResolvesAnotherUsersApproval()
    {
        var (runId, _, approvalId) = SeedParkedRun();

        var rejected = await RejectHandler().Handle(
            new RejectTaskCommand { TaskId = approvalId, TenantId = _tenantId, UserId = Guid.NewGuid() },
            CancellationToken.None);

        Assert.False(rejected);
        using var verify = NewContext();
        Assert.Equal(AgentRunStatuses.AwaitingApproval,
            verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId).Status);
    }

    // ── resolve-once ──────────────────────────────────────────────────────

    [Fact]
    public async Task SecondApproveOfTheSameId_CannotDoubleExecute_OrDoubleStepTheRun()
    {
        var (runId, _, approvalId) = SeedParkedRun();

        var first = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        using (var afterFirst = NewContext())
        {
            var run = afterFirst.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
            Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        }
        var completedAt = CompletedAt(runId);

        // A fresh handler/scope, exactly like a second HTTP request.
        var second = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, first.Outcome);
        Assert.Equal(ApproveTaskOutcome.Conflict, second.Outcome);
        // The side-effecting tool ran exactly once.
        Assert.Equal(1, _orchestrator.Calls);

        using var verify = NewContext();
        var after = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, after.Status);
        Assert.Equal(completedAt, after.CompletedAtUtc);
        // No duplicate step, no duplicate transcript entry, no duplicated result.
        Assert.Equal(2, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == runId));
        Assert.Equal(1, verify.ChatMessages.AsNoTracking().Count(m => m.Role == "assistant"));
        Assert.Equal(1, CountOccurrences(after.Result!, _orchestrator.Output));
    }

    [Fact]
    public async Task RejectAfterApprove_CannotReopenOrRestepTheTerminatedRun()
    {
        var (runId, _, approvalId) = SeedParkedRun();

        await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);
        var rejected = await RejectHandler().Handle(
            new RejectTaskCommand { TaskId = approvalId, TenantId = _tenantId, UserId = _userId, Comment = "late" },
            CancellationToken.None);

        Assert.False(rejected);

        using var verify = NewContext();
        var run = verify.AgentRuns.AsNoTracking().Single(r => r.Id == runId);
        Assert.Equal(AgentRunStatuses.CompletedAfterApproval, run.Status);
        Assert.Null(run.Error);
        Assert.Equal(2, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == runId));
    }

    // ── chat approvals are untouched ──────────────────────────────────────

    [Fact]
    public async Task Approve_ForAnOrdinaryChatApproval_ExecutesButWritesNoRunOrTranscriptRows()
    {
        // A chat session with no AgentRun parked on it: chat drives its own
        // continuation client-side, so the reconciler must be a complete no-op.
        var sessionId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        _db.ChatSessions.Add(new ChatSession { Id = sessionId, TenantId = _tenantId, UserId = _userId });
        _db.TaskApprovals.Add(NewApproval(approvalId, sessionId));
        _db.SaveChanges();

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, outcome.Outcome);
        Assert.Equal(_orchestrator.Output, outcome.Result);

        using var verify = NewContext();
        Assert.Empty(verify.AgentRunSteps.AsNoTracking().ToList());
        Assert.Empty(verify.ChatMessages.AsNoTracking().ToList());
    }

    [Fact]
    public async Task Approve_NeverResolvesARunBelongingToAnotherUsersSession()
    {
        var (foreignRunId, foreignSessionId, _) = SeedParkedRun(Guid.NewGuid(), Guid.NewGuid());
        // An approval the caller DOES own, pointing at the foreign run's session.
        var approvalId = Guid.NewGuid();
        _db.TaskApprovals.Add(NewApproval(approvalId, foreignSessionId));
        _db.SaveChanges();

        var outcome = await ApproveHandler().Handle(Approve(approvalId), CancellationToken.None);

        Assert.Equal(ApproveTaskOutcome.Completed, outcome.Outcome);
        using var verify = NewContext();
        // The tenant/user predicate keeps the other user's run parked and unstepped.
        var foreignRun = verify.AgentRuns.AsNoTracking().Single(r => r.Id == foreignRunId);
        Assert.Equal(AgentRunStatuses.AwaitingApproval, foreignRun.Status);
        Assert.Null(foreignRun.CompletedAtUtc);
        Assert.Equal(1, verify.AgentRunSteps.AsNoTracking().Count(s => s.AgentRunId == foreignRunId));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private ApproveTaskCommand Approve(Guid approvalId)
        => new() { TaskId = approvalId, TenantId = _tenantId, UserId = _userId };

    private ApproveTaskCommandHandler ApproveHandler()
        => new(NewContext(), _orchestrator, _protector, NullLogger<ApproveTaskCommandHandler>.Instance);

    private RejectTaskCommandHandler RejectHandler()
        => new(NewContext(), _protector, NullLogger<RejectTaskCommandHandler>.Instance);

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private DateTime? CompletedAt(Guid runId)
    {
        using var db = NewContext();
        return db.AgentRuns.AsNoTracking().Single(r => r.Id == runId).CompletedAtUtc;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private TaskApproval NewApproval(Guid approvalId, Guid sessionId) => new()
    {
        Id = approvalId,
        TenantId = _tenantId,
        UserId = _userId,
        ChatSessionId = sessionId,
        ActionName = GatedAction,
        ParametersJson = _protector.Protect(GatedPayload),
        Status = "Pending"
    };

    /// <summary>
    /// Reproduces the stuck state exactly as StreamAgentRunCommandHandler leaves it:
    /// a hidden IsAgentRun session, a run at AwaitingApproval with a null
    /// CompletedAtUtc, the gated attempt already captured as step 1 (observation =
    /// the [HITL_APPROVAL_REQUIRED] marker), and a Pending approval on that session.
    /// </summary>
    private (Guid RunId, Guid SessionId, Guid ApprovalId) SeedParkedRun(Guid? tenantId = null, Guid? userId = null)
    {
        var runTenantId = tenantId ?? _tenantId;
        var runUserId = userId ?? _userId;
        if (runTenantId != _tenantId)
        {
            _db.Tenants.Add(new Tenant { Id = runTenantId, Name = "Other" });
            _db.Users.Add(new User
            {
                Id = runUserId,
                TenantId = runTenantId,
                Username = $"u{runUserId:N}",
                Email = $"{runUserId:N}@t.test",
                PasswordHash = "x",
                Role = "User"
            });
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
            _db.TaskApprovals.Add(NewApproval(approvalId, session.Id));
        _db.SaveChanges();

        return (run.Id, session.Id, approvalId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>Model-free tool boundary: counts executions so double-execution is observable.</summary>
    private sealed class RecordingOrchestrator : IAgentOrchestrator
    {
        public const string FailureMessage = "tool blew up";

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string Output { get; set; } = "tool output";
        public bool Throw { get; set; }

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
            if (Throw) throw new InvalidOperationException(FailureMessage);
            return Task.FromResult(Output);
        }
    }
}
