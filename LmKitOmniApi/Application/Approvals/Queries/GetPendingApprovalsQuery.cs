using MediatR;

namespace LmKitOmniApi.Application.Approvals.Queries;

/// <summary>
/// Lists the caller's pending task approvals, newest first. Approvals past their
/// deadline are excluded even when the background sweeper has not yet marked them
/// Expired — the list must only ever offer actions that are still executable.
/// </summary>
public class GetPendingApprovalsQuery : IRequest<List<PendingApprovalDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>
/// Projection returned by <see cref="GetPendingApprovalsQuery"/>. Property
/// names and declaration order intentionally mirror the previous
/// anonymous-type projection so the serialized JSON shape is unchanged.
/// </summary>
public class PendingApprovalDto
{
    public Guid Id { get; set; }
    public string ActionName { get; set; } = string.Empty;
    /// <summary>
    /// The decrypted action payload (e.g. the SQL a write tool wants to run) so a
    /// human can meaningfully approve/reject it. Owner-scoped: only ever returned to
    /// the user the approval belongs to. Capped for display.
    /// </summary>
    public string Details { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When this approval stops being answerable. Always in the future for a row in this
    /// list. Carried so a client can show the remaining time and warn before the window
    /// closes; this round only makes the API honest and does not change the UI.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
