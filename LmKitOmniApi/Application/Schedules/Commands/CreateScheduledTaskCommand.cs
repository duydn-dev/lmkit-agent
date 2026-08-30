using MediatR;

namespace LmKitOmniApi.Application.Schedules.Commands;

public sealed class CreateScheduledTaskCommand : SaveScheduledTaskCommandBase, IRequest<SaveScheduledTaskResult>
{
}
