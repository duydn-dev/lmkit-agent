using LmKitOmniApi.Application.Notifications.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Notifications.Handlers;

public class MarkNotificationReadCommandHandler : IRequestHandler<MarkNotificationReadCommand, bool>
{
    private readonly HermesDbContext _db;

    public MarkNotificationReadCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<bool> Handle(MarkNotificationReadCommand request, CancellationToken cancellationToken)
    {
        // No IsRead filter so re-marking an already-read notification stays an idempotent 204.
        var updated = await _db.Notifications
            .Where(notification => notification.Id == request.NotificationId
                && notification.TenantId == request.TenantId
                && notification.UserId == request.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(notification => notification.IsRead, true), cancellationToken);
        return updated > 0;
    }
}
