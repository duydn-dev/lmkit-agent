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

    /// <summary>"Running" | "Completed" | "Failed" | "AwaitingApproval".</summary>
    [MaxLength(32)]
    public string Status { get; set; } = "Running";

    /// <summary>The synthesized final answer, once completed.</summary>
    public string? Result { get; set; }

    /// <summary>A short, user-safe error summary when the run failed.</summary>
    [MaxLength(2000)]
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

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
