using LmKitOmniApi.Application.McpServers.Commands;
using LmKitOmniApi.Application.McpServers.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/mcp-servers")]
[Authorize(Roles = "Admin")]
public sealed class McpServersController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public McpServersController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var servers = await _mediator.Send(new ListMcpServersQuery { TenantId = tenantId }, ct);
        return Ok(servers);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new CreateMcpServerCommand
        {
            TenantId = tenantId,
            Name = request.Name,
            Url = request.Url,
            Headers = request.Headers,
            ReplaceHeaders = request.ReplaceHeaders,
            IsActive = request.IsActive,
            TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations
        }, ct);

        return result.Status switch
        {
            McpServerMutationStatus.ValidationFailed => BadRequest(result.ErrorMessage),
            McpServerMutationStatus.NameConflict => Conflict(result.ErrorMessage),
            _ => CreatedAtAction(nameof(List), new { id = result.Server!.Id }, result.Server)
        };
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new UpdateMcpServerCommand
        {
            TenantId = tenantId,
            ServerId = id,
            Name = request.Name,
            Url = request.Url,
            Headers = request.Headers,
            ReplaceHeaders = request.ReplaceHeaders,
            IsActive = request.IsActive,
            TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations
        }, ct);

        return result.Status switch
        {
            McpServerMutationStatus.NotFound => NotFound(),
            McpServerMutationStatus.ValidationFailed => BadRequest(result.ErrorMessage),
            McpServerMutationStatus.NameConflict => Conflict(result.ErrorMessage),
            _ => NoContent()
        };
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var deleted = await _mediator.Send(new DeleteMcpServerCommand { TenantId = tenantId, ServerId = id }, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
