using MediatR;

namespace LmKitOmniApi.Application.Approvals.Commands;

/// <summary>
/// Approves and executes one pending task approval. Security-critical
/// semantics (owner scoping, atomic approve-once claim, permission re-check at
/// execution time inside the orchestrator) live in the handler and must not
/// change; the controller only maps the outcome to HTTP.
/// </summary>
public class ApproveTaskCommand : IRequest<ApproveTaskResult>
{
    public Guid TaskId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

public enum ApproveTaskOutcome
{
    /// <summary>No task with this id is visible to the caller (missing or cross-tenant/cross-user) — 404.</summary>
    NotFound,

    /// <summary>The task exists but was no longer Pending when the atomic claim ran — 409.</summary>
    Conflict,

    /// <summary>The approved tool executed successfully — 200.</summary>
    Completed,

    /// <summary>The approved tool threw; the task was marked Failed — 500.</summary>
    Failed
}

public class ApproveTaskResult
{
    public ApproveTaskOutcome Outcome { get; set; }

    /// <summary>Tool output; only populated when <see cref="Outcome"/> is <see cref="ApproveTaskOutcome.Completed"/>.</summary>
    public string? Result { get; set; }
}
