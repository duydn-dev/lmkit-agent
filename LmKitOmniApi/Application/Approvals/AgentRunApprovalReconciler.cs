using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Approvals;

/// <summary>
/// Closes the <see cref="AgentRun"/> lifecycle when a human resolves the
/// <see cref="TaskApproval"/> that the run is parked on.
///
/// <para><b>Why this exists.</b> A run that trips an approval gate is finalized as
/// <see cref="AgentRunStatuses.AwaitingApproval"/> with a null
/// <see cref="AgentRun.CompletedAtUtc"/>, and nothing else ever wrote
/// <see cref="AgentRun.Status"/> — approve/reject resolved only the
/// <c>task_approvals</c> row, so the run hung in that state forever, its step
/// timeline missing the call that actually executed and its result reachable only
/// in the approve endpoint's HTTP response.</para>
///
/// <para><b>Scope — approve now RESUMES, under stated conditions.</b> Every resolution
/// records the call as a real <see cref="AgentRunStep"/> and persists its output where
/// the user can read it. What happens to the run then depends on the decision:</para>
/// <list type="bullet">
/// <item><b>Rejected / failed / expired</b> → a TERMINAL state
/// (<see cref="AgentRunStatuses.Rejected"/> / <see cref="AgentRunStatuses.Failed"/> /
/// <see cref="AgentRunStatuses.Expired"/>) with <see cref="AgentRun.CompletedAtUtc"/>
/// set, exactly as before. Nothing to continue from.</item>
/// <item><b>Approved and executed</b>, with a continuation worker present in the process
/// and the run still inside its resume/step budget → the run goes back to
/// <see cref="AgentRunStatuses.Running"/> with
/// <see cref="AgentRun.ResumeState"/> = <see cref="AgentRunResumeStates.Pending"/>, and
/// <c>AgentRunResumeService</c> re-enters the ReAct loop with the approved observation
/// replayed as prior progress. The redesign that made this possible is that the ReAct
/// pass is history-free: its whole input is a query plus a context string, and both are
/// reconstructible from <c>agent_runs</c> + <c>agent_run_steps</c>, so nothing has to
/// survive in the process that started it.</item>
/// <item><b>Approved, but no worker is registered, or resuming is disabled, or the run
/// has exhausted its budget</b> → the previous behaviour, unchanged and still truthful:
/// <see cref="AgentRunStatuses.CompletedAfterApproval"/> with a completion time. A run
/// that ends after one approved tool call is a smaller answer than a resumed run would
/// give — but it is a truthful one, and it ends.</item>
/// </list>
/// <para>The distinction is never guessed: a continuation is queued only when the caller
/// hands in a plan, which only a host that registered the worker can do.</para>
///
/// <para><b>Chat is unaffected.</b> An approval raised by an ordinary chat turn has
/// no <see cref="AgentRun"/> for its session, so every entry point here no-ops and
/// returns <see cref="AgentRunResolution.NoParkedRun"/> — nothing is stepped and, in
/// particular, nothing is queued for a continuation. Chat already continues itself: the
/// client pushes the approved result back as the next user turn (<c>useHitlActions</c>
/// in <c>useChatStream.ts</c>). Nothing in the chat path changes.</para>
///
/// <para>Static by design: every call site (the approve handler, the reject handler
/// and the expiry sweeper) already owns a scoped <see cref="HermesDbContext"/>, so
/// nothing new needs registering in DI. The one thing a call site must supply is the
/// resume plan, and only the approve handler has one to give.</para>
/// </summary>
internal static class AgentRunApprovalReconciler
{
    /// <summary><see cref="AgentRunStep.Action"/> is <c>MaxLength(64)</c>; <see cref="TaskApproval.ActionName"/> is 128.</summary>
    private const int MaxActionChars = 64;

    /// <summary><see cref="AgentRun.Error"/> is <c>MaxLength(2000)</c>.</summary>
    private const int MaxErrorChars = 2000;

    /// <summary>
    /// The gated tool ran after approval: records the call as the run's next step and
    /// appends the output to the run result and to the run's session transcript. The run
    /// then either QUEUES a continuation of its ReAct loop or, when
    /// <paramref name="resume"/> is null / disabled / out of budget, ends at
    /// <see cref="AgentRunStatuses.CompletedAfterApproval"/> as it always did.
    /// </summary>
    /// <param name="resume">
    /// The continuation bounds, or <c>null</c> for "this process has no continuation
    /// worker, so never queue one". Supplied by <c>ApproveTaskCommandHandler</c> from an
    /// optional <c>AgentRunResumeQueue</c> — see that type for why PRESENCE, rather than
    /// a configuration flag, is what decides.
    /// </param>
    public static Task<AgentRunResolution> RecordApprovedExecutionAsync(
        HermesDbContext db, TaskApproval task, string input, string output,
        AgentRunResumeOptions? resume, CancellationToken ct)
    {
        var summary = $"Đã phê duyệt và thực thi công cụ \"{task.ActionName}\".\n\n{output}";
        return ResolveAsync(
            db, task,
            stepInput: input,
            stepObservation: output,
            terminalStatus: AgentRunStatuses.CompletedAfterApproval,
            appendToResult: summary,
            error: null,
            assistantMessage: summary,
            resume: resume,
            ct);
    }

    /// <summary>
    /// The approved tool threw: records the attempt as a step and moves the run to
    /// <see cref="AgentRunStatuses.Failed"/> with a user-safe error (never the exception).
    /// Never resumes — there is no observation to continue from.
    /// </summary>
    public static Task<AgentRunResolution> RecordFailedExecutionAsync(
        HermesDbContext db, TaskApproval task, string input, CancellationToken ct)
    {
        var summary = $"Hành động đã phê duyệt \"{task.ActionName}\" thất bại khi thực thi.";
        return ResolveAsync(
            db, task,
            stepInput: input,
            stepObservation: summary,
            terminalStatus: AgentRunStatuses.Failed,
            appendToResult: null,
            error: summary,
            assistantMessage: summary,
            resume: null,
            ct);
    }

    /// <summary>
    /// A human rejected the gated tool: records the refusal as a step and moves the
    /// run to <see cref="AgentRunStatuses.Rejected"/>. Never resumes — a refusal is the
    /// end of the plan, not an observation to plan from.
    /// </summary>
    public static Task<AgentRunResolution> RecordRejectionAsync(
        HermesDbContext db, TaskApproval task, string input, string? comment, CancellationToken ct)
    {
        var summary = string.IsNullOrWhiteSpace(comment)
            ? $"Người dùng đã từ chối hành động \"{task.ActionName}\"."
            : $"Người dùng đã từ chối hành động \"{task.ActionName}\": {comment}";
        return ResolveAsync(
            db, task,
            stepInput: input,
            stepObservation: summary,
            terminalStatus: AgentRunStatuses.Rejected,
            appendToResult: null,
            error: summary,
            assistantMessage: summary,
            resume: null,
            ct);
    }

    /// <summary>
    /// Nobody answered in time: the approval lapsed to
    /// <see cref="TaskApproval.ExpiredStatus"/>, so the run it parked is closed at
    /// <see cref="AgentRunStatuses.Expired"/> with the lapse recorded as a step. No tool
    /// ran, so there is no result to append — only a truthful explanation of why the run
    /// stopped.
    ///
    /// <para>This is the one entry point NOT driven by a human request: the background
    /// sweeper calls it after atomically claiming the row. That claim, plus the
    /// <see cref="AgentRunStatuses.AwaitingApproval"/> predicate in <c>ResolveAsync</c>,
    /// is what keeps a sweep idempotent — a second pass finds nothing to claim, and even
    /// if it did, the run has already left AwaitingApproval and cannot be stepped twice.</para>
    /// </summary>
    public static Task<AgentRunResolution> RecordExpirationAsync(
        HermesDbContext db, TaskApproval task, string input, CancellationToken ct)
    {
        var summary =
            $"Yêu cầu phê duyệt hành động \"{task.ActionName}\" đã hết hạn mà không có ai phản hồi. "
            + "Hành động KHÔNG được thực thi.";
        return ResolveAsync(
            db, task,
            stepInput: input,
            stepObservation: summary,
            terminalStatus: AgentRunStatuses.Expired,
            appendToResult: null,
            error: summary,
            assistantMessage: summary,
            resume: null,
            ct);
    }

    /// <summary>
    /// Finds the run parked on <paramref name="task"/> and resolves it in one
    /// SaveChanges. The <see cref="AgentRunStatuses.AwaitingApproval"/> predicate is
    /// what makes this idempotent: a second resolution of the same approval (or of a
    /// second approval belonging to an already-resolved run) matches nothing and
    /// no-ops, so a run can never be stepped, scored, or QUEUED FOR RESUME twice. That
    /// last one matters most now — the predicate is what stops a double-clicked approve
    /// from buying two continuation passes, and it holds for two replicas racing just as
    /// well as for two clicks, because leaving AwaitingApproval is a single UPDATE.
    /// </summary>
    private static async Task<AgentRunResolution> ResolveAsync(
        HermesDbContext db,
        TaskApproval task,
        string stepInput,
        string stepObservation,
        string terminalStatus,
        string? appendToResult,
        string? error,
        string? assistantMessage,
        AgentRunResumeOptions? resume,
        CancellationToken ct)
    {
        // Tracked on purpose — the run is mutated below and flushed with the inserts.
        var run = await db.AgentRuns
            .Where(r => r.ChatSessionId == task.ChatSessionId
                && r.TenantId == task.TenantId
                && r.UserId == task.UserId
                && r.Status == AgentRunStatuses.AwaitingApproval)
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (run is null) return AgentRunResolution.NoParkedRun;

        var lastOrdinal = await db.AgentRunSteps
            .Where(step => step.AgentRunId == run.Id)
            .Select(step => (int?)step.Ordinal)
            .MaxAsync(ct) ?? 0;

        db.AgentRunSteps.Add(new AgentRunStep
        {
            AgentRunId = run.Id,
            Ordinal = lastOrdinal + 1,
            Action = Truncate(task.ActionName, MaxActionChars),
            Input = stepInput,
            Observation = stepObservation
        });

        // Continue the ReAct loop instead of ending here, when there is something to
        // continue FROM (an approved observation), somebody to drive it (a registered
        // worker → non-null plan, enabled), and budget left. Every other case keeps the
        // truthful terminal status — including a cap being hit, which is stated in the
        // result rather than left for the reader to infer from a missing answer.
        var resumeStopReason = ResumeStopReason(resume, run, stepCount: lastOrdinal + 1, terminalStatus);
        var resuming = resumeStopReason is null;

        // The run's hidden IsAgentRun session is the HITL substrate and already holds
        // the turn's transcript, so the resolution belongs in it as an assistant turn.
        // An ephemeral session persists nothing, by its own contract.
        if (assistantMessage is not null && !await IsEphemeralSessionAsync(db, run.ChatSessionId, ct))
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ChatSessionId = run.ChatSessionId,
                Role = "assistant",
                Content = resuming ? assistantMessage + "\n\n" + ContinuingNote : assistantMessage,
                CreatedAt = DateTime.UtcNow
            });
        }

        if (appendToResult is not null) run.Result = Compose(run.Result, appendToResult);
        if (error is not null) run.Error = Truncate(error, MaxErrorChars);

        if (resumeStopReason is null)
        {
            // Back to Running rather than to a status word of its own: the run genuinely
            // is planning again, "Running" is what every existing reader (the run list,
            // the agent-run page's status pill) already renders for that, and the
            // continuation's own bookkeeping lives on its own column instead of
            // overloading a vocabulary other code depends on.
            run.Status = AgentRunStatuses.Running;
            run.ResumeState = AgentRunResumeStates.Pending;
            run.ResumeLeaseUntilUtc = null;
            run.CompletedAtUtc = null;
        }
        else
        {
            run.Status = terminalStatus;
            run.CompletedAtUtc = DateTime.UtcNow;
            if (resumeStopReason.Length > 0) run.Result = Compose(run.Result, resumeStopReason);
        }

        await db.SaveChangesAsync(ct);
        return resuming ? AgentRunResolution.QueuedForResume : AgentRunResolution.Terminal;
    }

    /// <summary>
    /// Why this resolution will NOT continue the run, or <c>null</c> when it will. An
    /// empty string means "no continuation was ever on the table" (a rejection, a
    /// failure, an expiry, or a host with no worker) and needs no explanation in the
    /// result; a non-empty one is a bound that was hit and is written where the user can
    /// read it.
    /// </summary>
    private static string? ResumeStopReason(
        AgentRunResumeOptions? resume, AgentRun run, int stepCount, string terminalStatus)
    {
        if (resume is null || !resume.Enabled) return string.Empty;
        if (terminalStatus != AgentRunStatuses.CompletedAfterApproval) return string.Empty;
        if (resume.EffectiveMaxResumesPerRun <= 0) return string.Empty;

        if (run.ResumeCount >= resume.EffectiveMaxResumesPerRun)
            return $"Lần chạy đã được tiếp tục {run.ResumeCount} lần sau phê duyệt và đã đạt giới hạn, nên dừng tại đây.";

        if (stepCount >= resume.EffectiveMaxTotalSteps)
            return $"Lần chạy đã đạt giới hạn {stepCount} bước công cụ, nên dừng tại đây thay vì tiếp tục.";

        return null;
    }

    private const string ContinuingNote = "Agent đang tiếp tục lần chạy với kết quả này.";

    private static Task<bool> IsEphemeralSessionAsync(HermesDbContext db, Guid sessionId, CancellationToken ct)
        => db.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.IsEphemeral, ct);

    private static string Compose(string? existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : existing + "\n\n" + addition;

    private static string Truncate(string value, int max)
        => value.Length > max ? value[..max] : value;
}
