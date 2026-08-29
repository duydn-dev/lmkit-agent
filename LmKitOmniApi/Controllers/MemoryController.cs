using LmKitOmniApi.Application.Memory.Commands;
using LmKitOmniApi.Application.Memory.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class MemoryController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public MemoryController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var memories = await _mediator.Send(new ListAgentMemoriesQuery
        {
            TenantId = tenantId,
            UserId = userId
        }, ct);

        return Ok(memories);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Forget(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var deleted = await _mediator.Send(new DeleteAgentMemoryCommand
        {
            TenantId = tenantId,
            UserId = userId,
            MemoryId = id
        }, ct);

        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var confirmed = await _mediator.Send(new ConfirmAgentMemoryCommand
        {
            TenantId = tenantId,
            UserId = userId,
            MemoryId = id
        }, ct);

        return confirmed ? NoContent() : NotFound();
    }
}
