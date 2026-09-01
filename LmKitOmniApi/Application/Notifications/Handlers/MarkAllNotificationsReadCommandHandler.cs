using LmKitOmniApi.Application.Notifications.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Notifications.Handlers;

public class MarkAllNotificationsReadCommandHandler : IRequestHandler<MarkAllNotificationsReadCommand, int>
{
    private readonly HermesDbContext _db;

    public MarkAllNotificationsReadCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<int> Handle(MarkAllNotificationsReadCommand request, CancellationToken cancellationToken)
    {
        return await _db.Notifications
            .Where(notification => notification.TenantId == request.TenantId
                && notification.UserId == request.UserId
                && !notification.IsRead)
            .ExecuteUpdateAsync(update => update.SetProperty(notification => notification.IsRead, true), cancellationToken);
    }
}
