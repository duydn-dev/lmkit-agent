using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Schedules.Handlers;

public class DeleteScheduledTaskCommandHandler : IRequestHandler<DeleteScheduledTaskCommand, bool>
{
    private readonly HermesDbContext _db;

    public DeleteScheduledTaskCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<bool> Handle(DeleteScheduledTaskCommand request, CancellationToken cancellationToken)
    {
        var deleted = await _db.ScheduledTasks
            .Where(task => task.Id == request.TaskId
                && task.TenantId == request.TenantId
                && task.UserId == request.UserId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }
}
