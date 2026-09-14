using LmKitOmniApi.Application.Schedules.Commands;
using MediatR;

namespace LmKitOmniApi.Application.Schedules.Queries;

/// <summary>Lists the caller's scheduled tasks, newest first.</summary>
public sealed class ListScheduledTasksQuery : IRequest<Common.PagedResult<ScheduledTaskDto>>
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = Common.Paging.DefaultPageSize;
    public string? Search { get; set; }
}
