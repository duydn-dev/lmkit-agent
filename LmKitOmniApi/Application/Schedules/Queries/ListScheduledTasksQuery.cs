using LmKitOmniApi.Application.Schedules.Commands;
using MediatR;

namespace LmKitOmniApi.Application.Schedules.Queries;

/// <summary>Lists the caller's scheduled tasks, newest first.</summary>
public sealed class ListScheduledTasksQuery : IRequest<List<ScheduledTaskDto>>
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
}
