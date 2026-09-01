using MediatR;

namespace LmKitOmniApi.Application.Notifications.Commands;

/// <summary>Marks every unread notification of the caller as read (single set-based update).</summary>
public sealed class MarkAllNotificationsReadCommand : IRequest<int>
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
}
