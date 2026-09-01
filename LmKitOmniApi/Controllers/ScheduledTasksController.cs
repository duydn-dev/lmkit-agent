using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Application.Schedules.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// CRUD for the caller's scheduled prompts (<c>/api/schedules</c>). Every action is
/// tenant+user scoped from claims: a task owned by someone else is a 404, never a 403.
/// </summary>
[ApiController]
[Route("api/schedules")]
[Authorize]
public sealed class ScheduledTasksController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public ScheduledTasksController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var tasks = await _mediator.Send(new ListScheduledTasksQuery { TenantId = tenantId, UserId = userId }, ct);
        return Ok(tasks);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveScheduledTaskRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new CreateScheduledTaskCommand
        {
            TenantId = tenantId,
            UserId = userId,
            Name = request.Name,
            Prompt = request.Prompt,
            ScheduleKind = request.ScheduleKind,
            IntervalMinutes = request.IntervalMinutes,
            TimeOfDayMinutes = request.TimeOfDayMinutes,
            DayOfWeek = request.DayOfWeek
        }, ct);

        return result.Status switch
        {
            ScheduledTaskMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => CreatedAtAction(nameof(List), new { id = result.Task!.Id }, result.Task)
        };
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveScheduledTaskRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new UpdateScheduledTaskCommand
        {
            TenantId = tenantId,
            UserId = userId,
            TaskId = id,
            Name = request.Name,
            Prompt = request.Prompt,
            ScheduleKind = request.ScheduleKind,
            IntervalMinutes = request.IntervalMinutes,
            TimeOfDayMinutes = request.TimeOfDayMinutes,
            DayOfWeek = request.DayOfWeek
        }, ct);

        return result.Status switch
        {
            ScheduledTaskMutationStatus.NotFound => NotFound(),
            ScheduledTaskMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => NoContent()
        };
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var deleted = await _mediator.Send(new DeleteScheduledTaskCommand
        {
            TenantId = tenantId,
            UserId = userId,
            TaskId = id
        }, ct);

        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new ToggleScheduledTaskCommand
        {
            TenantId = tenantId,
            UserId = userId,
            TaskId = id
        }, ct);

        return result.Status switch
        {
            ScheduledTaskMutationStatus.NotFound => NotFound(),
            ScheduledTaskMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => NoContent()
        };
    }
}
