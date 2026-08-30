using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;

namespace LmKitOmniApi.Application.Schedules.Handlers;

public class CreateScheduledTaskCommandHandler : IRequestHandler<CreateScheduledTaskCommand, SaveScheduledTaskResult>
{
    private readonly HermesDbContext _db;

    public CreateScheduledTaskCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<SaveScheduledTaskResult> Handle(CreateScheduledTaskCommand request, CancellationToken cancellationToken)
    {
        var error = ScheduledTaskRules.Validate(request);
        if (error is not null) return SaveScheduledTaskResult.ValidationFailed(error);

        // New tasks are always created enabled, so the enabled cap gates creation.
        var enabledCount = await ScheduledTaskRules.CountEnabledAsync(_db, request.TenantId, request.UserId, cancellationToken);
        if (enabledCount >= ScheduledTaskRules.MaxEnabledTasksPerUser)
            return SaveScheduledTaskResult.ValidationFailed(ScheduledTaskRules.EnabledCapMessage);

        var task = new ScheduledTask
        {
            TenantId = request.TenantId,
            UserId = request.UserId,
            Enabled = true
        };
        ScheduledTaskRules.Apply(task, request, DateTime.UtcNow);

        _db.ScheduledTasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken);

        return SaveScheduledTaskResult.Success(ScheduledTaskRules.ToDto(task));
    }
}
