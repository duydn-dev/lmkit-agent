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
/// <see cref="AwaitingApproval"/> is the only non-terminal resting state. It is
/// left by <c>AgentRunApprovalReconciler</c> when the human resolves the gating
/// <see cref="Domain.Entities.TaskApproval"/> — approve resolves to
/// <see cref="CompletedAfterApproval"/> (or <see cref="Failed"/> when the tool
/// throws), reject resolves to <see cref="Rejected"/>. Nothing else ever moves a
/// run out of it, so an approval that is never answered leaves the run parked
/// here indefinitely (see AGENTRUN-FIX-INTEGRATION.md — "expiry" is the one
/// remaining hole and needs a background sweeper).
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
}
