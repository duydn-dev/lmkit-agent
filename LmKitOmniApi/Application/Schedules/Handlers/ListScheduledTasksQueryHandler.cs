using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Application.Schedules.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Common;

namespace LmKitOmniApi.Application.Schedules.Handlers;

public class ListScheduledTasksQueryHandler : IRequestHandler<ListScheduledTasksQuery, Common.PagedResult<ScheduledTaskDto>>
{
    private readonly HermesDbContext _db;

    public ListScheduledTasksQueryHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<Common.PagedResult<ScheduledTaskDto>> Handle(ListScheduledTasksQuery request, CancellationToken cancellationToken)
    {
        var query = _db.ScheduledTasks
            .AsNoTracking()
            .Where(task => task.TenantId == request.TenantId && task.UserId == request.UserId);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(task => EF.Functions.Like(task.Name, pattern) || EF.Functions.Like(task.Prompt, pattern));
        }

        return await query
            .OrderByDescending(task => task.CreatedAtUtc)
            .Select(task => new ScheduledTaskDto
            {
                Id = task.Id,
                Name = task.Name,
                Prompt = task.Prompt,
                RunMode = task.RunMode,
                CustomAgentId = task.CustomAgentId,
                DeliveryWebhookUrl = task.DeliveryWebhookUrl,
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
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}
