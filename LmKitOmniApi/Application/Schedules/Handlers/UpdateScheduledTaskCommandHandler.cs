using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Schedules.Handlers;

public class UpdateScheduledTaskCommandHandler : IRequestHandler<UpdateScheduledTaskCommand, SaveScheduledTaskResult>
{
    private readonly HermesDbContext _db;

    public UpdateScheduledTaskCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<SaveScheduledTaskResult> Handle(UpdateScheduledTaskCommand request, CancellationToken cancellationToken)
    {
        var task = await _db.ScheduledTasks.SingleOrDefaultAsync(
            candidate => candidate.Id == request.TaskId
                && candidate.TenantId == request.TenantId
                && candidate.UserId == request.UserId,
            cancellationToken);
        if (task is null) return SaveScheduledTaskResult.NotFound();

        var error = ScheduledTaskRules.Validate(request);
        if (error is not null) return SaveScheduledTaskResult.ValidationFailed(error);

        // Enabled is not part of the save body — it only changes through the toggle endpoint.
        ScheduledTaskRules.Apply(task, request, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);

        return SaveScheduledTaskResult.Success();
    }
}
