using MediatR;

namespace LmKitOmniApi.Application.Notifications.Commands;

/// <summary>Marks one owner-scoped notification read; returns <c>false</c> when no row matched (→ 404).</summary>
public sealed class MarkNotificationReadCommand : IRequest<bool>
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public Guid NotificationId { get; init; }
}
