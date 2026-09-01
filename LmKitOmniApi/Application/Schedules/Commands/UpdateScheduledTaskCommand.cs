using MediatR;

namespace LmKitOmniApi.Application.Schedules.Commands;

public sealed class UpdateScheduledTaskCommand : SaveScheduledTaskCommandBase, IRequest<SaveScheduledTaskResult>
{
    public Guid TaskId { get; set; }
}
