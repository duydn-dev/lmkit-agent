using System.Security.Claims;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class MemoryController : ControllerBase
{
    private readonly HermesDbContext _dbContext;
    private readonly IAgentMemoryService _memoryService;

    public MemoryController(HermesDbContext dbContext, IAgentMemoryService memoryService)
    {
        _dbContext = dbContext;
        _memoryService = memoryService;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var memories = await _dbContext.AgentMemories
            .AsNoTracking()
            .Where(memory => memory.TenantId == tenantId
                && memory.UserId == userId
                && (memory.ExpiresAtUtc == null || memory.ExpiresAtUtc > DateTime.UtcNow))
            .OrderByDescending(memory => memory.UpdatedAtUtc)
            .Select(memory => new
            {
                memory.Id,
                memory.MemoryType,
                memory.MemoryKey,
                memory.MemoryValue,
                memory.Confidence,
                memory.IsConfirmed,
                memory.ExpiresAtUtc,
                memory.CreatedAtUtc,
                memory.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        return Ok(memories);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Forget(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var deleted = await _memoryService.DeleteMemoryAsync(tenantId, userId, id, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var confirmed = await _memoryService.ConfirmMemoryAsync(tenantId, userId, id, ct);
        return confirmed ? NoContent() : NotFound();
    }

    private bool TryGetIdentity(out Guid tenantId, out Guid userId)
    {
        var tenantValid = Guid.TryParse(User.FindFirst("TenantId")?.Value, out tenantId);
        var userValid = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
        return tenantValid && userValid;
    }
}
