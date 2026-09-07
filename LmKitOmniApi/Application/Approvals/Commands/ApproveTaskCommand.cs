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

    /// <summary>
    /// The task is still Pending but is past its <c>ExpiresAtUtc</c>, so the gated tool
    /// was NOT executed — 410.
    ///
    /// <para>Distinct from <see cref="Conflict"/> on purpose. Conflict means somebody
    /// already decided; this means nobody did, in time. The action was proposed against a
    /// situation that no longer exists, and a human clicking "approve" on a stale row in a
    /// long queue is signing for context they no longer have — so the only safe answer is
    /// to refuse and make the reason legible, rather than report a race that did not
    /// happen.</para>
    /// </summary>
    Expired,

    /// <summary>The approved tool executed successfully — 200.</summary>
    Completed,

    /// <summary>
    /// The human's YES was recorded but nothing executed HERE — 202.
    ///
    /// <para>Used for a <c>COMPUTER_USE</c> approval. The click / type / navigate it
    /// describes is performed by the computer-use loop, inside its own browser container,
    /// against an observation only that loop holds; this endpoint cannot replay it and the
    /// loop is the thing waiting for the answer. Routing it through the tool dispatcher
    /// (which is what "approve" used to do) could only fail — no role grants a
    /// <c>COMPUTER_USE</c> tool permission — and the gate reads that failure as a
    /// REJECTION, so clicking "approve" used to reject the action. Recording the decision
    /// on the row is the whole job: the waiting gate polls exactly this status.</para>
    /// </summary>
    Recorded,

    /// <summary>The approved tool threw; the task was marked Failed — 500.</summary>
    Failed
}

public class ApproveTaskResult
{
    public ApproveTaskOutcome Outcome { get; set; }

    /// <summary>Tool output; only populated when <see cref="Outcome"/> is <see cref="ApproveTaskOutcome.Completed"/>.</summary>
    public string? Result { get; set; }
}
