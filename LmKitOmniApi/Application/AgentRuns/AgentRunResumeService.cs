using LmKitOmniApi.Services;
using System.Text;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LMKit.TextGeneration.Chat;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// Drives the continuation of an <see cref="AgentRun"/> that a human unblocked by
/// approving the tool call it was parked on: re-enters the ReAct loop with the approved
/// observation fed back in, records whatever further steps the agent takes, and lands the
/// run on a terminal status — or parks it again if it hits a SECOND gate.
///
/// <para><b>The problem this solves, and how.</b> The orchestrator's ReAct pass is an
/// in-process <c>await foreach</c>; its state dies with the HTTP request that started it,
/// while an approval can arrive hours later on a different replica. So nothing in memory
/// can be resumed. What makes a real resume possible anyway is that the pass is
/// history-free by construction — its entire input is a query string plus a context
/// string — and every part of that input is already persisted:
/// <c>agent_runs.Goal</c> and the ordered <c>agent_run_steps</c>, whose last row is the
/// approved call together with its real output (written by
/// <c>AgentRunApprovalReconciler</c>), plus the scope snapshot on the approval row. This
/// service reconstructs the input from those rows and runs the pass again. No new
/// continuation state is invented beyond one marker column saying which runs are owed a
/// pass (<see cref="AgentRun.ResumeState"/>).</para>
///
/// <para><b>Exactly once, across replicas and double-clicks.</b> Every transition here is
/// a conditional <c>ExecuteUpdate</c>, the same mechanism the approve/reject handlers and
/// the expiry sweeper already use. The claim is a compare-and-swap on
/// (<see cref="AgentRun.ResumeState"/>, <see cref="AgentRun.ResumeCount"/>) which also
/// increments the counter, so two workers racing for the same run produce exactly one
/// winner and the loser matches nothing. The counter is then the FENCING TOKEN: the
/// release at the end of a pass is conditioned on it, so a worker whose lease expired
/// while it was running discovers it lost ownership and discards its work instead of
/// writing a second copy of the steps. Integers were chosen for the token over the lease
/// timestamp precisely because they survive a database round-trip exactly.</para>
///
/// <para><b>Bounded, always.</b> A pass runs outside any HTTP request and takes the
/// single chat inference lease, so it is capped four ways: resumes per run, total steps
/// per run, runs per poll, and a claim lease that lets a crashed pass be retaken without
/// letting it be retaken forever (the retake spends budget too). A run that exhausts any
/// bound is CLOSED at <c>CompletedAfterApproval</c> — the same truthful terminal state a
/// run reached before this feature existed — never left queued.</para>
/// </summary>
public sealed class AgentRunResumeService
{
    private readonly HermesDbContext _db;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentRunHistoryFactory _historyFactory;
    private readonly AgentRunResumeOptions _options;
    private readonly ILogger<AgentRunResumeService> _logger;

    /// <summary><see cref="AgentRun.Error"/> is <c>MaxLength(2000)</c>.</summary>
    private const int MaxErrorChars = 2000;

    /// <summary><see cref="AgentRunStep.Action"/> is <c>MaxLength(64)</c>.</summary>
    private const int MaxActionChars = 64;

    /// <summary>How much of a prior answer is replayed into the synthesis history.</summary>
    private const int MaxHistoryResultChars = 4000;

    /// <summary>
    /// Pages a disabled-feature drain will read before giving up for this start. Large
    /// enough to clear any realistic backlog in one go, finite so a pathological
    /// "row that will not close" cannot spin the worker.
    /// </summary>
    private const int MaxDrainBatches = 1_000;

    public AgentRunResumeService(
        HermesDbContext db,
        IAgentOrchestrator orchestrator,
        IAgentRunHistoryFactory historyFactory,
        AgentRunResumeQueue queue,
        ILogger<AgentRunResumeService> logger)
    {
        _db = db;
        _orchestrator = orchestrator;
        _historyFactory = historyFactory;
        _options = queue.Options;
        _logger = logger;
    }

    /// <summary>
    /// Drives up to <see cref="AgentRunResumeOptions.MaxRunsPerPass"/> queued
    /// continuations, one after another. Runs that are past a bound are closed truthfully
    /// instead of resumed, so a pass always makes progress and the queue always drains.
    /// </summary>
    /// <returns>How many runs this pass actually resumed.</returns>
    public async Task<int> ResumePendingAsync(DateTime nowUtc, CancellationToken ct)
    {
        var resumed = 0;
        for (var i = 0; i < _options.EffectiveMaxRunsPerPass; i++)
        {
            // Stop, do not throw. Everything already driven in this pass has been
            // persisted (and re-queued where it was cut short), so a shutdown between
            // runs is a clean end to the pass, not a failure to report as one.
            if (ct.IsCancellationRequested) break;

            var candidate = await NextCandidateAsync(nowUtc, ct);
            if (candidate is null) break;

            // Budget checks BEFORE the claim, so an exhausted run is closed rather than
            // re-selected on every poll for the rest of the process's life.
            if (candidate.ResumeCount >= _options.EffectiveMaxResumesPerRun)
            {
                await CloseWithoutResumingAsync(candidate.Id, BudgetNote(candidate.ResumeCount), ct);
                continue;
            }

            var stepCount = await _db.AgentRunSteps.CountAsync(step => step.AgentRunId == candidate.Id, ct);
            if (stepCount >= _options.EffectiveMaxTotalSteps)
            {
                await CloseWithoutResumingAsync(candidate.Id, StepCapNote(stepCount), ct);
                continue;
            }

            var token = candidate.ResumeCount + 1;
            if (!await TryClaimAsync(candidate.Id, candidate.ResumeCount, nowUtc, ct)) continue;

            if (await DriveAsync(candidate.Id, token, ct))
            {
                // The gate turned this attempt away; see DriveAsync. Ending the pass here is
                // what keeps a persistently busy permit from burning every queued run's
                // budget in seconds -- the next poll retries after the interval.
                break;
            }
            resumed++;

            nowUtc = DateTime.UtcNow;
        }

        return resumed;
    }

    /// <summary>
    /// Closes every queued or claimed continuation without running it, at the truthful
    /// <c>CompletedAfterApproval</c>. Called by the worker when the feature is switched
    /// off, so turning it off can never strand a run that was queued while it was on.
    /// </summary>
    /// <returns>How many runs were closed.</returns>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var closed = 0;
        // Paged, and drains the WHOLE backlog rather than one page: the worker stops
        // after this call, so anything left behind would sit queued until the feature is
        // switched back on — which is the stranded state this drain exists to prevent.
        // Rows leave the predicate as they are closed, so the next page is the next batch
        // without an offset.
        for (var batch = 0; batch < MaxDrainBatches; batch++)
        {
            var ids = await _db.AgentRuns
                .AsNoTracking()
                .Where(run => run.ResumeState != null)
                .OrderBy(run => run.CreatedAtUtc)
                .Select(run => run.Id)
                .Take(_options.EffectiveMaxRunsPerPass)
                .ToListAsync(ct);
            if (ids.Count == 0) break;

            var before = closed;
            foreach (var id in ids)
                if (await CloseWithoutResumingAsync(id, DisabledNote, ct)) closed++;

            // Nothing in this page could be closed (every row was taken by somebody
            // else). Re-reading the same page would spin, so stop and let the next start
            // pick up whatever is genuinely left.
            if (closed == before) break;
        }

        if (closed > 0)
            _logger.LogInformation("Closed {Count} queued agent-run continuation(s) because resuming is disabled.", closed);
        return closed;
    }

    // ── selection + claim ─────────────────────────────────────────────────

    /// <summary>
    /// The next run owed a continuation: queued, or claimed by a worker whose lease has
    /// lapsed (its process died mid-pass). Oldest first, so a backlog drains in order.
    /// </summary>
    private Task<Candidate?> NextCandidateAsync(DateTime nowUtc, CancellationToken ct)
        => _db.AgentRuns
            .AsNoTracking()
            .Where(run => run.ResumeState == AgentRunResumeStates.Pending
                || (run.ResumeState == AgentRunResumeStates.Claimed
                    && (run.ResumeLeaseUntilUtc == null || run.ResumeLeaseUntilUtc <= nowUtc)))
            .OrderBy(run => run.CreatedAtUtc)
            .Select(run => new Candidate(run.Id, run.ResumeState!, run.ResumeCount))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Compare-and-swap on (<see cref="AgentRun.ResumeState"/>,
    /// <see cref="AgentRun.ResumeCount"/>): claims the run for a continuation only if it
    /// is still exactly as <paramref name="observedResumeCount"/> saw it, and in the same
    /// statement takes ownership and spends a unit of resume budget.
    ///
    /// <para>That single UPDATE is the whole concurrency story. Two workers — two
    /// replicas, or two passes on one — that both read the same queued run both try this,
    /// and the counter predicate means the second matches zero rows. The winner's new
    /// counter value is also the FENCING TOKEN <see cref="ReleaseClaimAsync"/> checks, so
    /// a claim that silently expired cannot write its results afterwards.</para>
    ///
    /// <para>Internal so the compare-and-swap itself is testable head-on, with two callers
    /// acting on the same observation, instead of only through a timing-dependent race.</para>
    /// </summary>
    internal async Task<bool> TryClaimAsync(
        Guid runId, int observedResumeCount, DateTime nowUtc, CancellationToken ct)
    {
        var leaseUntil = nowUtc + _options.Lease;
        var claimed = await _db.AgentRuns
            .Where(run => run.Id == runId
                && run.ResumeCount == observedResumeCount
                && (run.ResumeState == AgentRunResumeStates.Pending
                    || (run.ResumeState == AgentRunResumeStates.Claimed
                        && (run.ResumeLeaseUntilUtc == null || run.ResumeLeaseUntilUtc <= nowUtc))))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.ResumeState, AgentRunResumeStates.Claimed)
                .SetProperty(run => run.ResumeLeaseUntilUtc, leaseUntil)
                .SetProperty(run => run.ResumeCount, run => run.ResumeCount + 1),
                ct);

        if (claimed == 0)
        {
            _logger.LogDebug("Lost the race to resume agent run {RunId}; another worker owns it.", runId);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Releases a claim we still own, moving the run to <paramref name="newState"/>
    /// (<c>null</c> = the continuation machinery is done with it,
    /// <see cref="AgentRunResumeStates.Pending"/> = put it back in the queue). Guarded by
    /// the fencing token, so a worker whose lease lapsed mid-pass learns it lost the run
    /// and writes nothing.
    /// </summary>
    private async Task<bool> ReleaseClaimAsync(Guid runId, int token, string? newState, CancellationToken ct)
    {
        var released = await _db.AgentRuns
            .Where(run => run.Id == runId
                && run.ResumeState == AgentRunResumeStates.Claimed
                && run.ResumeCount == token)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.ResumeState, newState)
                .SetProperty(run => run.ResumeLeaseUntilUtc, (DateTime?)null),
                ct);
        return released > 0;
    }

    // ── the continuation itself ───────────────────────────────────────────

    /// <returns>
    /// <c>true</c> when the inference admission queue turned this attempt away. The caller
    /// ends its sweep on that: every other candidate would meet the same busy permit, and the
    /// run just requeued is Pending again -- re-picking it in the same pass would spend its
    /// whole resume budget in one hot loop.
    /// </returns>
    private async Task<bool> DriveAsync(Guid runId, int token, CancellationToken ct)
    {
        var run = await _db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            _logger.LogWarning("Agent run {RunId} vanished between claim and resume.", runId);
            return false;
        }

        // Authority is re-checked at continuation time, exactly like the approve path
        // re-checks tool permissions: an approval bought ONE decision, not standing
        // authority for a user who has since been deactivated or moved tenant.
        var storedRole = await _db.Users
            .Where(user => user.Id == run.UserId && user.TenantId == run.TenantId && user.IsActive)
            .Select(user => user.Role)
            .FirstOrDefaultAsync(ct);
        if (storedRole is null)
        {
            _logger.LogWarning("Not resuming agent run {RunId}: its user is inactive or no longer in the tenant.", runId);
            await FinishAsync(runId, token, AgentRunStatuses.Failed, steps: [], appendToResult: null,
                error: "Không thể tiếp tục: tài khoản không còn hoạt động trong tổ chức này.");
            return false;
        }
        var agentRole = storedRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";

        var priorSteps = await _db.AgentRunSteps
            .AsNoTracking()
            .Where(step => step.AgentRunId == runId)
            .OrderBy(step => step.Ordinal)
            .Select(step => new AgentRunResumePrompt.Step(step.Action, step.Input, step.Observation))
            .ToListAsync(ct);

        var query = AgentRunResumePrompt.Compose(run.Goal, priorSteps, _options);

        // The continuation may never be WIDER than the turn that requested the approval.
        // Same intersection the approved execution itself ran under (snapshot on the
        // approval row ∩ the session's bound agent, read live) — a resumed pass plans
        // with tools, so getting this wrong would hand it authority the gated turn never
        // had.
        var scope = await AgentOrchestrator.ResolveApprovedActionOptionsAsync(
            _db, run.TenantId, run.UserId, await GatingApprovalIdAsync(run, ct), ct);

        var history = await BuildHistoryAsync(run, ct);
        var sink = new List<AgentRunStepData>();
        var content = new StringBuilder();
        var gated = false;
        var completed = false;
        var cancelled = false;
        var capacityRefused = false;
        string? failure = null;

        try
        {
            await foreach (var text in _orchestrator.StreamProcessQueryAsync(
                run.TenantId, run.ChatSessionId, run.UserId, agentRole, query, history, scope, ct, sink))
            {
                content.Append(text);
                if (text.StartsWith(AgentRunMarkers.ApprovalRequired, StringComparison.Ordinal)) gated = true;
            }
            completed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure. The model lease is released by the orchestrator's
            // own `await using` as this enumerator unwinds; what we owe the run is to put
            // it back in the queue rather than to fail it.
            cancelled = true;
        }
        catch (InferenceQueueRejectedException ex)
        {
            // Capacity, not failure: the single chat permit stayed busy past the queue's
            // bound. The run did nothing wrong, so it goes back in the queue rather than to
            // Failed. The claim already spent one resume, and MaxResumesPerRun is what bounds
            // sustained overload -- past that budget the run closes truthfully, saying why.
            _logger.LogWarning(
                "Resume of agent run {RunId} turned away by the {Gate} inference queue ({Reason}); requeueing.",
                runId, ex.Gate, ex.Reason);
            capacityRefused = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resuming agent run {RunId} failed.", runId);
            failure = "Không thể tiếp tục lần chạy sau khi phê duyệt.";
        }

        var answer = AgentRunMarkers.StripMarkers(content.ToString());

        if ((cancelled || capacityRefused) && !gated)
        {
            // Steps that really happened are kept; the pass is re-queued and replays them
            // as prior progress next time. ResumeCount was already spent by the claim, so
            // this cannot loop forever.
            // The refusal step is the orchestrator telling the TIMELINE why this attempt
            // stopped; it is not prior progress, and replaying it into the next attempt's
            // prompt would read as something the agent did.
            var progress = capacityRefused
                ? sink.Where(step => step.Action != AgentRunStepData.AdmissionRefusedAction).ToList()
                : sink;
            await FinishAsync(runId, token, AgentRunStatuses.Running, progress,
                appendToResult: string.IsNullOrWhiteSpace(answer) ? null : answer,
                error: null, requeue: true);
            return capacityRefused;
        }

        if (gated)
        {
            // A SECOND gate. Back to the one non-terminal resting state, with the new
            // approval's marker persisted as a step — which is how both the reconciler
            // (session + AwaitingApproval) and the agent-run page (last marker step) find
            // it. Approving again queues another continuation, budget permitting.
            await FinishAsync(runId, token, AgentRunStatuses.AwaitingApproval, sink,
                appendToResult: string.IsNullOrWhiteSpace(answer) ? null : answer, error: null);
            return false;
        }

        if (!completed)
        {
            await FinishAsync(runId, token, AgentRunStatuses.Failed, sink,
                appendToResult: string.IsNullOrWhiteSpace(answer) ? null : answer, error: failure);
            return false;
        }

        await FinishAsync(runId, token, AgentRunStatuses.Completed, sink,
            appendToResult: string.IsNullOrWhiteSpace(answer) ? null : answer, error: null);
        return false;
    }

    /// <summary>
    /// The approval that gated this run — the newest one raised on its hidden session.
    /// Only its stored SCOPE is used; the decision itself is already reflected in the
    /// step the reconciler wrote.
    /// </summary>
    private Task<Guid?> GatingApprovalIdAsync(AgentRun run, CancellationToken ct)
        => _db.TaskApprovals
            .AsNoTracking()
            .Where(approval => approval.ChatSessionId == run.ChatSessionId
                && approval.TenantId == run.TenantId
                && approval.UserId == run.UserId)
            .OrderByDescending(approval => approval.CreatedAtUtc)
            .Select(approval => (Guid?)approval.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The synthesis pass is history-aware, so it gets the run's own transcript back:
    /// the goal as the user turn and what has been answered so far as the assistant turn.
    /// Reconstructed from the run row rather than read out of the hidden session's
    /// messages because the run row is the authority on both and cannot drift from it.
    /// </summary>
    private async Task<ChatHistory> BuildHistoryAsync(AgentRun run, CancellationToken ct)
    {
        var history = await _historyFactory.CreateAsync(ct);
        history.AddMessage(AuthorRole.User, run.Goal);
        if (!string.IsNullOrWhiteSpace(run.Result))
            history.AddMessage(AuthorRole.Assistant, Truncate(run.Result!, MaxHistoryResultChars));
        return history;
    }

    // ── persistence ───────────────────────────────────────────────────────

    /// <summary>
    /// Persists one continuation's outcome: releases the claim (fenced), appends the
    /// steps it produced and moves the run to <paramref name="status"/>. Always with
    /// <see cref="CancellationToken.None"/> — the work already happened, and a shutdown
    /// must not be the reason it goes unrecorded.
    /// </summary>
    private async Task FinishAsync(
        Guid runId,
        int token,
        string status,
        IReadOnlyList<AgentRunStepData> steps,
        string? appendToResult,
        string? error,
        bool requeue = false)
    {
        var ct = CancellationToken.None;

        // Release first, guarded by the fencing token. Losing here means another worker
        // retook this run after our lease lapsed and is (or was) driving it — writing our
        // steps now would duplicate its timeline, so we drop the work instead.
        if (!await ReleaseClaimAsync(runId, token, requeue ? AgentRunResumeStates.Pending : null, ct))
        {
            _logger.LogWarning(
                "Discarding a resumed pass for agent run {RunId}: its claim was taken over before the pass finished.",
                runId);
            return;
        }

        // With ResumeState no longer Claimed-by-us nothing else can select this run, so
        // the rest is a plain scoped write.
        var run = await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        var lastOrdinal = await _db.AgentRunSteps
            .Where(step => step.AgentRunId == runId)
            .Select(step => (int?)step.Ordinal)
            .MaxAsync(ct) ?? 0;

        foreach (var step in steps)
        {
            lastOrdinal++;
            _db.AgentRunSteps.Add(new AgentRunStep
            {
                AgentRunId = runId,
                Ordinal = lastOrdinal,
                Action = Truncate(step.Action, MaxActionChars),
                Input = step.Input,
                Observation = step.Observation
            });
        }

        run.Status = status;
        if (appendToResult is not null) run.Result = Compose(run.Result, appendToResult);
        if (error is not null) run.Error = Truncate(error, MaxErrorChars);
        // Running (a re-queued pass) and AwaitingApproval (a second gate) are the two
        // non-terminal outcomes; everything else is final.
        run.CompletedAtUtc =
            status is AgentRunStatuses.Running or AgentRunStatuses.AwaitingApproval ? null : DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ends a queued continuation without running it, at the truthful
    /// <c>CompletedAfterApproval</c>, and says in the result WHY the agent stopped there.
    /// Reached when a bound is hit or the feature is off — never silently.
    /// </summary>
    private async Task<bool> CloseWithoutResumingAsync(Guid runId, string note, CancellationToken ct)
    {
        // Atomic: whoever clears ResumeState owns the close. A worker on another replica
        // doing the same thing at the same time matches nothing.
        var claimed = await _db.AgentRuns
            .Where(run => run.Id == runId && run.ResumeState != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.ResumeState, (string?)null)
                .SetProperty(run => run.ResumeLeaseUntilUtc, (DateTime?)null),
                ct);
        if (claimed == 0) return false;

        var run = await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return false;

        run.Status = AgentRunStatuses.CompletedAfterApproval;
        run.Result = Compose(run.Result, note);
        run.CompletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Closed agent run {RunId} at CompletedAfterApproval without resuming: {Note}", runId, note);
        return true;
    }

    private const string DisabledNote =
        "Tính năng tiếp tục lần chạy sau phê duyệt đang tắt, nên lần chạy kết thúc ở kết quả của công cụ đã được phê duyệt.";

    private static string BudgetNote(int resumeCount) =>
        $"Lần chạy đã được tiếp tục {resumeCount} lần sau phê duyệt và đã đạt giới hạn, nên dừng tại đây.";

    private static string StepCapNote(int stepCount) =>
        $"Lần chạy đã đạt giới hạn {stepCount} bước công cụ, nên dừng tại đây thay vì tiếp tục.";

    private static string Compose(string? existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : existing + "\n\n" + addition;

    private static string Truncate(string value, int max)
        => value.Length > max ? value[..max] : value;

    private sealed record Candidate(Guid Id, string ResumeState, int ResumeCount);
}
