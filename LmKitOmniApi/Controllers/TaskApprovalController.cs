using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TaskApprovalController : ApiControllerBase
{
    private readonly HermesDbContext _dbContext;
    private readonly IAgentOrchestrator _agentOrchestrator;
    private readonly ILogger<TaskApprovalController> _logger;
    private readonly TaskApprovalPayloadProtector _payloadProtector;

    public TaskApprovalController(
        HermesDbContext dbContext,
        IAgentOrchestrator agentOrchestrator,
        TaskApprovalPayloadProtector payloadProtector,
        ILogger<TaskApprovalController> logger)
    {
        _dbContext = dbContext;
        _agentOrchestrator = agentOrchestrator;
        _payloadProtector = payloadProtector;
        _logger = logger;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingApprovals()
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        var pending = await _dbContext.TaskApprovals
            .Where(t => t.TenantId == tenantId && t.UserId == userId && t.Status == "Pending")
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new { t.Id, t.ActionName, t.CreatedAtUtc })
            .ToListAsync();

        return Ok(pending);
    }

    [HttpPost("{id}/approve")]
    public async Task<IActionResult> ApproveTask(Guid id)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        var task = await _dbContext.TaskApprovals
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId && t.UserId == userId);
        if (task == null) return NotFound("Task not found.");

        // Atomically claim the task. Two concurrent approval requests must never
        // execute the same side-effecting tool twice.
        var claimed = await _dbContext.TaskApprovals
            .Where(t => t.Id == id
                && t.TenantId == tenantId
                && t.UserId == userId
                && t.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, "Executing")
                .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow),
                HttpContext.RequestAborted);

        if (claimed == 0)
            return Conflict("Task is no longer pending.");
        
        // Execute tool directly
        string result;
        try
        {
            var parameters = _payloadProtector.Unprotect(task.ParametersJson);
            result = await _agentOrchestrator.ExecuteDirectActionAsync(
                tenantId,
                userId,
                task.ActionName,
                parameters,
                id,
                HttpContext.RequestAborted);

            await _dbContext.TaskApprovals
                .Where(t => t.Id == id && t.Status == "Executing")
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.Status, "Completed"),
                    HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing approved task.");
            await _dbContext.TaskApprovals
                .Where(t => t.Id == id && t.Status == "Executing")
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.Status, "Failed"),
                    HttpContext.RequestAborted);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Success = false,
                Error = "Approved task execution failed. See server logs for details."
            });
        }

        return Ok(new { Success = true, Result = result });
    }

    [HttpPost("{id}/reject")]
    public async Task<IActionResult> RejectTask(Guid id, [FromBody] RejectTaskRequest request)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
            return Unauthorized();

        var rejected = await _dbContext.TaskApprovals
            .Where(t => t.Id == id
                && t.TenantId == tenantId
                && t.UserId == userId
                && t.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, "Rejected")
                .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow)
                .SetProperty(t => t.RejectionComment, request.Comment),
                HttpContext.RequestAborted);

        if (rejected == 0) return NotFound("Pending task not found.");

        return Ok(new { Success = true, Message = "Task rejected." });
    }
}

public class RejectTaskRequest
{
    public string? Comment { get; set; }
}
