using MediatR;

namespace LmKitOmniApi.Application.Approvals.Queries;

/// <summary>Lists the caller's pending task approvals, newest first.</summary>
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
}
