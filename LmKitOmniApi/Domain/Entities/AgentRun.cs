using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A goal-oriented autonomous agent run: the agent plans and executes tools over
/// several ReAct iterations toward a single stated goal, streaming its steps. It
/// reuses the chat orchestrator (RBAC / HITL / audit / sandbox all apply) and is
/// backed by a hidden <see cref="ChatSession"/> (IsAgentRun) so approvals and
/// message persistence work without polluting the chat list.
/// </summary>
[Table("agent_runs")]
public sealed class AgentRun
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The hidden chat session this run executes under (substrate for HITL + history).</summary>
    public Guid ChatSessionId { get; set; }

    [MaxLength(4000)]
    public string Goal { get; set; } = string.Empty;

    /// <summary>
    /// One of <c>AgentRunStatuses</c> — that type carries the complete vocabulary and
    /// says which values are terminal.
    /// </summary>
    [MaxLength(32)]
    public string Status { get; set; } = "Running";

    /// <summary>The synthesized final answer, once completed.</summary>
    public string? Result { get; set; }

    /// <summary>A short, user-safe error summary when the run failed.</summary>
    [MaxLength(2000)]
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Durable continuation marker for a run that has to re-enter the ReAct loop after a
    /// human approved the tool call it was parked on. <c>null</c> for every run that is
    /// not owed a continuation; otherwise one of <c>AgentRunResumeStates</c>
    /// (<c>Pending</c> = queued, <c>Claimed</c> = a worker is driving it right now).
    ///
    /// <para><b>Why a column and not an in-memory queue.</b> The orchestrator's ReAct
    /// pass is an in-process <c>await foreach</c> whose state dies with the HTTP request
    /// that started it, and an approval can land hours later, on a different replica.
    /// The continuation therefore has to be reconstructible from the database alone —
    /// and it is: <see cref="Goal"/> plus the ordered <see cref="Steps"/> (whose last
    /// entry is the approved call and its real output) plus the approval row's own scope
    /// snapshot are the ENTIRE input that pass takes, because it carries no session
    /// history by design. This column is the only genuinely new state: which runs are
    /// owed a continuation, and whether somebody is already giving them one.</para>
    /// </summary>
    [MaxLength(16)]
    public string? ResumeState { get; set; }

    /// <summary>
    /// How many continuation passes have been CLAIMED for this run. Incremented by the
    /// claim itself rather than by a successful finish, so a pass that faults or is cut
    /// short by a shutdown still spends budget — the bounded direction. Capped by
    /// <c>AgentRunResumeOptions.MaxResumesPerRun</c>; a run that reaches the cap is
    /// closed at <c>CompletedAfterApproval</c>, the same truthful terminal state a run
    /// with no resume at all reaches.
    /// </summary>
    public int ResumeCount { get; set; }

    /// <summary>
    /// When the current <c>Claimed</c> continuation stops being owned by whoever claimed
    /// it. Without it, a worker that dies mid-resume would leave the run <c>Claimed</c>
    /// forever; past this instant another worker (or the same one after a restart) may
    /// retake it. Null whenever <see cref="ResumeState"/> is not <c>Claimed</c>.
    /// </summary>
    public DateTime? ResumeLeaseUntilUtc { get; set; }

    public ICollection<AgentRunStep> Steps { get; set; } = new List<AgentRunStep>();
}

/// <summary>
/// One recorded tool invocation within an <see cref="AgentRun"/>: the action the
/// agent chose, the input it passed, and the (untrusted) observation it got back.
/// Captured at the orchestrator's single tool seam, in execution order.
/// </summary>
[Table("agent_run_steps")]
public sealed class AgentRunStep
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }

    /// <summary>1-based position of this step within its run.</summary>
    public int Ordinal { get; set; }

    [MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    public string Input { get; set; } = string.Empty;

    public string Observation { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public AgentRun? AgentRun { get; set; }
}
