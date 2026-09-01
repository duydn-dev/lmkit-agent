using MediatR;

namespace LmKitOmniApi.Application.Notifications.Queries;

/// <summary>Lists the caller's latest notifications (max 50), newest first.</summary>
public sealed class ListNotificationsQuery : IRequest<List<NotificationDto>>
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public bool UnreadOnly { get; init; }
}

/// <summary>
/// Wire DTO for GET <c>/api/notifications</c>. Serialized camelCase:
/// <c>{ id, type, title, body, isRead, createdAt }</c> — <c>createdAt</c> maps from
/// <c>Notification.CreatedAtUtc</c>. The legacy Document* columns are deliberately not exposed.
/// </summary>
public sealed class NotificationDto
{
    public Guid Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public bool IsRead { get; init; }
    public DateTime CreatedAt { get; init; }
}
