using MediatR;

namespace LmKitOmniApi.Application.Schedules.Commands;

/// <summary>Owner-scoped delete; the handler returns <c>false</c> when no row matched (→ 404).</summary>
public sealed class DeleteScheduledTaskCommand : IRequest<bool>
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public Guid TaskId { get; init; }
}
