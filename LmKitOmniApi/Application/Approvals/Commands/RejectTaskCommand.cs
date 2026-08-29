using MediatR;

namespace LmKitOmniApi.Application.Approvals.Commands;

/// <summary>
/// Rejects one pending task approval owned by the caller. Returns <c>false</c>
/// when no owned pending task matched, which the controller maps to 404.
/// </summary>
public class RejectTaskCommand : IRequest<bool>
{
    public Guid TaskId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string? Comment { get; set; }
}
