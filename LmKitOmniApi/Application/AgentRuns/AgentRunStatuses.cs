namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// The complete <see cref="Domain.Entities.AgentRun.Status"/> vocabulary — one
/// source of truth for the run lifecycle, so a producer and a reader can never
/// disagree on a spelling.
///
/// <para>
/// Every value except <see cref="Running"/> and <see cref="AwaitingApproval"/> is
/// TERMINAL: <see cref="Domain.Entities.AgentRun.CompletedAtUtc"/> is set and the
/// run never changes again.
/// </para>
///
/// <para>
/// <see cref="AwaitingApproval"/> is the only non-terminal resting state, and it is
/// now BOUNDED. It is left by <c>AgentRunApprovalReconciler</c> when the gating
/// <see cref="Domain.Entities.TaskApproval"/> is resolved — a human's approve
/// resolves to <see cref="CompletedAfterApproval"/> (or <see cref="Failed"/> when the
/// tool throws), a human's reject resolves to <see cref="Rejected"/>, and an approval
/// that no human answers before its <c>ExpiresAtUtc</c> is swept to
/// <see cref="Expired"/> by <c>ApprovalExpirySweeper</c>. With that last edge in
/// place every run reaches a terminal state on its own; nothing parks here forever.
/// </para>
/// </summary>
public static class AgentRunStatuses
{
    /// <summary>Non-terminal: the ReAct loop is streaming.</summary>
    public const string Running = "Running";

    /// <summary>Terminal: the ReAct loop ran to completion and synthesized an answer.</summary>
    public const string Completed = "Completed";

    /// <summary>Terminal: the run was cancelled mid-stream or threw.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// Non-terminal: the run yielded at a human-in-the-loop gate and is parked on a
    /// pending <see cref="Domain.Entities.TaskApproval"/> for the same chat session.
    /// </summary>
    public const string AwaitingApproval = "AwaitingApproval";

    /// <summary>
    /// Terminal: a human approved the gated tool and it executed successfully. The
    /// tool output is recorded as the run's last <see cref="Domain.Entities.AgentRunStep"/>
    /// and appended to <see cref="Domain.Entities.AgentRun.Result"/>.
    ///
    /// <para>
    /// Deliberately NOT <see cref="Completed"/>: the ReAct loop is not resumed after
    /// an approval (see <c>AgentRunApprovalReconciler</c> remarks), so the run ends
    /// with the raw tool observation rather than a synthesized final answer. The
    /// distinct status keeps that difference visible instead of pretending the agent
    /// finished reasoning.
    /// </para>
    /// </summary>
    public const string CompletedAfterApproval = "CompletedAfterApproval";

    /// <summary>Terminal: a human rejected the gated tool, so the run stops without executing it.</summary>
    public const string Rejected = "Rejected";

    /// <summary>
    /// Terminal: nobody answered the gating <see cref="Domain.Entities.TaskApproval"/>
    /// before it expired, so the background sweeper closed the run. The gated tool did
    /// NOT execute — semantically this is closer to <see cref="Rejected"/> than to any
    /// success, but it is kept distinct so "a human said no" is never confused with
    /// "nobody looked", which is an operational signal about the approval queue rather
    /// than a decision about the action.
    /// </summary>
    public const string Expired = "Expired";
}
