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
/// <para><b>Scope — this is NOT a resume.</b> Genuinely resuming the run would mean
/// re-entering the ReAct loop with the approved observation fed back as a tool
/// result and letting the agent plan its next step; that needs a durable
/// continuation of the streaming loop (the orchestrator's ReAct pass is an
/// in-process <c>await foreach</c> whose state dies with the request) and is a
/// redesign, not a fix. What this type does instead is make the lifecycle HONEST:
/// the approved call is recorded as a real <see cref="AgentRunStep"/>, its output is
/// persisted where the user can read it, and the run reaches a TERMINAL state
/// (<see cref="AgentRunStatuses.CompletedAfterApproval"/> /
/// <see cref="AgentRunStatuses.Rejected"/> / <see cref="AgentRunStatuses.Failed"/>)
/// with <see cref="AgentRun.CompletedAtUtc"/> set. A run that ends after one
/// approved tool call is a smaller answer than a resumed run would give — but it is
/// a truthful one, and it ends.</para>
///
/// <para><b>Chat is unaffected.</b> An approval raised by an ordinary chat turn has
/// no <see cref="AgentRun"/> for its session, so every entry point here no-ops and
/// returns <c>false</c>. Chat already continues itself: the client pushes the
/// approved result back as the next user turn (<c>useHitlActions</c> in
/// <c>useChatStream.ts</c>). Nothing in the chat path changes.</para>
///
/// <para>Static by design: both call sites already own a scoped
/// <see cref="HermesDbContext"/>, so nothing new needs registering in DI.</para>
/// </summary>
internal static class AgentRunApprovalReconciler
{
    /// <summary><see cref="AgentRunStep.Action"/> is <c>MaxLength(64)</c>; <see cref="TaskApproval.ActionName"/> is 128.</summary>
    private const int MaxActionChars = 64;

    /// <summary><see cref="AgentRun.Error"/> is <c>MaxLength(2000)</c>.</summary>
    private const int MaxErrorChars = 2000;

    /// <summary>
    /// The gated tool ran after approval: records the call as the run's next step,
    /// appends the output to the run result and to the run's session transcript, and
    /// moves the run to <see cref="AgentRunStatuses.CompletedAfterApproval"/>.
    /// </summary>
    /// <returns><c>true</c> when a parked run was resolved; <c>false</c> for a plain chat approval.</returns>
    public static Task<bool> RecordApprovedExecutionAsync(
        HermesDbContext db, TaskApproval task, string input, string output, CancellationToken ct)
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
            ct);
    }

    /// <summary>
    /// The approved tool threw: records the attempt as a step and moves the run to
    /// <see cref="AgentRunStatuses.Failed"/> with a user-safe error (never the exception).
    /// </summary>
    public static Task<bool> RecordFailedExecutionAsync(
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
            ct);
    }

    /// <summary>
    /// A human rejected the gated tool: records the refusal as a step and moves the
    /// run to <see cref="AgentRunStatuses.Rejected"/>.
    /// </summary>
    public static Task<bool> RecordRejectionAsync(
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
            ct);
    }

    /// <summary>
    /// Finds the run parked on <paramref name="task"/> and closes it in one
    /// SaveChanges. The <see cref="AgentRunStatuses.AwaitingApproval"/> predicate is
    /// what makes this idempotent: a second resolution of the same approval (or of a
    /// second approval belonging to an already-closed run) matches nothing and
    /// no-ops, so a run can never be stepped or scored twice.
    /// </summary>
    private static async Task<bool> ResolveAsync(
        HermesDbContext db,
        TaskApproval task,
        string stepInput,
        string stepObservation,
        string terminalStatus,
        string? appendToResult,
        string? error,
        string? assistantMessage,
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

        if (run is null) return false;

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

        // The run's hidden IsAgentRun session is the HITL substrate and already holds
        // the turn's transcript, so the resolution belongs in it as an assistant turn.
        // An ephemeral session persists nothing, by its own contract.
        if (assistantMessage is not null && !await IsEphemeralSessionAsync(db, run.ChatSessionId, ct))
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ChatSessionId = run.ChatSessionId,
                Role = "assistant",
                Content = assistantMessage,
                CreatedAt = DateTime.UtcNow
            });
        }

        run.Status = terminalStatus;
        if (appendToResult is not null) run.Result = Compose(run.Result, appendToResult);
        if (error is not null) run.Error = Truncate(error, MaxErrorChars);
        run.CompletedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static Task<bool> IsEphemeralSessionAsync(HermesDbContext db, Guid sessionId, CancellationToken ct)
        => db.ChatSessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.IsEphemeral, ct);

    private static string Compose(string? existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : existing + "\n\n" + addition;

    private static string Truncate(string value, int max)
        => value.Length > max ? value[..max] : value;
}
