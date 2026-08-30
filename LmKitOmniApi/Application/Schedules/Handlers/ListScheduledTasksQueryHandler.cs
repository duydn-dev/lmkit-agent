using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Application.Schedules.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Schedules.Handlers;

public class ListScheduledTasksQueryHandler : IRequestHandler<ListScheduledTasksQuery, List<ScheduledTaskDto>>
{
    private readonly HermesDbContext _db;

    public ListScheduledTasksQueryHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<List<ScheduledTaskDto>> Handle(ListScheduledTasksQuery request, CancellationToken cancellationToken)
    {
        return await _db.ScheduledTasks
            .AsNoTracking()
            .Where(task => task.TenantId == request.TenantId && task.UserId == request.UserId)
            .OrderByDescending(task => task.CreatedAtUtc)
            .Select(task => new ScheduledTaskDto
            {
                Id = task.Id,
                Name = task.Name,
                Prompt = task.Prompt,
                ScheduleKind = task.ScheduleKind,
                IntervalMinutes = task.IntervalMinutes,
                TimeOfDayMinutes = task.TimeOfDayMinutes,
                DayOfWeek = task.DayOfWeek,
                Enabled = task.Enabled,
                NextRunUtc = task.NextRunUtc,
                LastRunUtc = task.LastRunUtc,
                LastStatus = task.LastStatus,
                LastError = task.LastError
            })
            .ToListAsync(cancellationToken);
    }
}
