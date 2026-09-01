using LmKitOmniApi.Application.DatabaseConnections.Commands;
using LmKitOmniApi.Application.DatabaseConnections.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Admin management of external database connections the agent can query
/// READ-ONLY. Admin-only and tenant-scoped (id resolved within the tenant, never
/// from the body). Connection strings are encrypted at rest and never returned.
/// </summary>
[ApiController]
[Route("api/database-connections")]
[Authorize(Roles = "Admin")]
public sealed class DatabaseConnectionsController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public DatabaseConnectionsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        return Ok(await _mediator.Send(new GetDatabaseConnectionsQuery { TenantId = tenantId }, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveDatabaseConnectionRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var result = await _mediator.Send(new CreateDatabaseConnectionCommand
        {
            TenantId = tenantId,
            UserId = userId,
            Request = request
        }, ct);
        return result.Success ? Ok(new { id = result.Id }) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveDatabaseConnectionRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var result = await _mediator.Send(new UpdateDatabaseConnectionCommand
        {
            Id = id,
            TenantId = tenantId,
            UserId = userId,
            Request = request
        }, ct);
        if (result.Success) return NoContent();
        return result.Error == "Không tìm thấy kết nối." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var deleted = await _mediator.Send(new DeleteDatabaseConnectionCommand { Id = id, TenantId = tenantId }, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var result = await _mediator.Send(new TestDatabaseConnectionCommand { Id = id, TenantId = tenantId }, ct);
        return result.Success ? Ok(new { success = true }) : BadRequest(new { success = false, message = result.Error });
    }
}
