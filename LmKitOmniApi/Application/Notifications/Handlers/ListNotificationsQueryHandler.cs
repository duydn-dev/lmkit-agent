using LmKitOmniApi.Application.Notifications.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Notifications.Handlers;

public class ListNotificationsQueryHandler : IRequestHandler<ListNotificationsQuery, List<NotificationDto>>
{
    private const int MaxNotifications = 50;

    private readonly HermesDbContext _db;

    public ListNotificationsQueryHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<List<NotificationDto>> Handle(ListNotificationsQuery request, CancellationToken cancellationToken)
    {
        var query = _db.Notifications
            .AsNoTracking()
            .Where(notification => notification.TenantId == request.TenantId
                && notification.UserId == request.UserId);

        if (request.UnreadOnly)
            query = query.Where(notification => !notification.IsRead);

        return await query
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Take(MaxNotifications)
            .Select(notification => new NotificationDto
            {
                Id = notification.Id,
                Type = notification.Type,
                Title = notification.Title,
                Body = notification.Body,
                IsRead = notification.IsRead,
                CreatedAt = notification.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
    }
}
