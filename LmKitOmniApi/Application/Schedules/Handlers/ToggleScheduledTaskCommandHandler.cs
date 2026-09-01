using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Schedules.Handlers;

public class ToggleScheduledTaskCommandHandler : IRequestHandler<ToggleScheduledTaskCommand, SaveScheduledTaskResult>
{
    private readonly HermesDbContext _db;

    public ToggleScheduledTaskCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<SaveScheduledTaskResult> Handle(ToggleScheduledTaskCommand request, CancellationToken cancellationToken)
    {
        var task = await _db.ScheduledTasks.SingleOrDefaultAsync(
            candidate => candidate.Id == request.TaskId
                && candidate.TenantId == request.TenantId
                && candidate.UserId == request.UserId,
            cancellationToken);
        if (task is null) return SaveScheduledTaskResult.NotFound();

        if (task.Enabled)
        {
            task.Enabled = false;
        }
        else
        {
            var enabledCount = await ScheduledTaskRules.CountEnabledAsync(_db, request.TenantId, request.UserId, cancellationToken);
            if (enabledCount >= ScheduledTaskRules.MaxEnabledTasksPerUser)
                return SaveScheduledTaskResult.ValidationFailed(ScheduledTaskRules.EnabledCapMessage);

            task.Enabled = true;
            // Re-anchor the schedule so a long-disabled task does not fire immediately
            // for every missed occurrence, and clear any stale worker lease.
            task.NextRunUtc = ScheduleCalculator.ComputeNextRun(task, DateTime.UtcNow);
            task.ClaimedUntilUtc = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return SaveScheduledTaskResult.Success();
    }
}
