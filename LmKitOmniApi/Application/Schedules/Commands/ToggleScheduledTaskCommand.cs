using MediatR;

namespace LmKitOmniApi.Application.Schedules.Commands;

/// <summary>
/// Flips <c>Enabled</c> on an owner-scoped task. Enabling recomputes <c>NextRunUtc</c> and
/// enforces the per-user enabled-task cap.
/// </summary>
public sealed class ToggleScheduledTaskCommand : IRequest<SaveScheduledTaskResult>
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public Guid TaskId { get; init; }
}
