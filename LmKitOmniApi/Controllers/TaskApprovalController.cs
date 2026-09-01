using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MediatR;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Application.Approvals.Queries;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TaskApprovalController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public TaskApprovalController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingApprovals()
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        var pending = await _mediator.Send(new GetPendingApprovalsQuery
        {
            TenantId = tenantId,
            UserId = userId
        }, HttpContext.RequestAborted);

        return Ok(pending);
    }

    [HttpPost("{id}/approve")]
    public async Task<IActionResult> ApproveTask(Guid id)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        var outcome = await _mediator.Send(new ApproveTaskCommand
        {
            TaskId = id,
            TenantId = tenantId,
            UserId = userId
        }, HttpContext.RequestAborted);

        if (outcome.Outcome == ApproveTaskOutcome.NotFound)
            return NotFound("Task not found.");

        if (outcome.Outcome == ApproveTaskOutcome.Conflict)
            return Conflict("Task is no longer pending.");

        if (outcome.Outcome == ApproveTaskOutcome.Failed)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Success = false,
                Error = "Approved task execution failed. See server logs for details."
            });
        }

        return Ok(new { Success = true, Result = outcome.Result });
    }

    [HttpPost("{id}/reject")]
    public async Task<IActionResult> RejectTask(Guid id, [FromBody] RejectTaskRequest request)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        var rejected = await _mediator.Send(new RejectTaskCommand
        {
            TaskId = id,
            TenantId = tenantId,
            UserId = userId,
            Comment = request.Comment
        }, HttpContext.RequestAborted);

        if (!rejected) return NotFound("Pending task not found.");

        return Ok(new { Success = true, Message = "Task rejected." });
    }
}

public class RejectTaskRequest
{
    public string? Comment { get; set; }
}
