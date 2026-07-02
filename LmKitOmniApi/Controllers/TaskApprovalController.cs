using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Abstractions;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TaskApprovalController : ControllerBase
{
    private readonly HermesDbContext _dbContext;
    private readonly IAgentOrchestrator _agentOrchestrator;
    private readonly ILogger<TaskApprovalController> _logger;

    public TaskApprovalController(
        HermesDbContext dbContext,
        IAgentOrchestrator agentOrchestrator,
        ILogger<TaskApprovalController> logger)
    {
        _dbContext = dbContext;
        _agentOrchestrator = agentOrchestrator;
        _logger = logger;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingApprovals()
    {
        var tenantIdStr = User.FindFirst("TenantId")?.Value;
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (!Guid.TryParse(tenantIdStr, out var tenantId) || !Guid.TryParse(userIdStr, out var userId))
            return Unauthorized();

        var pending = await _dbContext.TaskApprovals
            .Where(t => t.TenantId == tenantId && t.UserId == userId && t.Status == "Pending")
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new { t.Id, t.ActionName, t.ParametersJson, t.CreatedAtUtc })
            .ToListAsync();

        return Ok(pending);
    }

    [HttpPost("{id}/approve")]
    public async Task<IActionResult> ApproveTask(Guid id)
    {
        var tenantIdStr = User.FindFirst("TenantId")?.Value;
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        if (!Guid.TryParse(tenantIdStr, out var tenantId) || !Guid.TryParse(userIdStr, out var userId))
            return Unauthorized();

        var task = await _dbContext.TaskApprovals.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId);
        if (task == null) return NotFound("Task not found.");
        if (task.Status != "Pending") return BadRequest($"Task is already {task.Status}.");

        task.Status = "Approved";
        task.ResolvedAtUtc = DateTime.UtcNow;
        
        // Execute tool directly
        string result;
        try
        {
            result = await _agentOrchestrator.ExecuteDirectActionAsync(tenantId, userId, task.ActionName, task.ParametersJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing approved task.");
            result = $"[Error executing task: {ex.Message}]";
        }

        await _dbContext.SaveChangesAsync();

        return Ok(new { Success = true, Result = result });
    }

    [HttpPost("{id}/reject")]
    public async Task<IActionResult> RejectTask(Guid id, [FromBody] RejectTaskRequest request)
    {
        var tenantIdStr = User.FindFirst("TenantId")?.Value;
        if (!Guid.TryParse(tenantIdStr, out var tenantId)) return Unauthorized();

        var task = await _dbContext.TaskApprovals.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId);
        if (task == null) return NotFound("Task not found.");
        if (task.Status != "Pending") return BadRequest($"Task is already {task.Status}.");

        task.Status = "Rejected";
        task.ResolvedAtUtc = DateTime.UtcNow;
        task.RejectionComment = request.Comment;

        await _dbContext.SaveChangesAsync();

        return Ok(new { Success = true, Message = "Task rejected." });
    }
}

public class RejectTaskRequest
{
    public string? Comment { get; set; }
}
