using LmKitOmniApi.Application.CustomAgents;
using LmKitOmniApi.Application.CustomAgents.Commands;
using LmKitOmniApi.Application.CustomAgents.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// CRUD surface for user-authored custom agents (Gems/GPTs style): a persona
/// prompt plus an optional tool whitelist and optional pinned knowledge
/// documents. Every user manages their own agents; agents marked
/// IsSharedWithTenant are usable (but not editable) by the whole tenant.
/// </summary>
[ApiController]
[Route("api/agents/custom")]
[Authorize]
public sealed class CustomAgentsController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public CustomAgentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Agents visible to the caller: own agents + tenant-shared agents.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var agents = await _mediator.Send(new GetCustomAgentsQuery { TenantId = tenantId, UserId = userId }, ct);
        return Ok(agents);
    }

    /// <summary>
    /// The selectable tool catalog — the permission tool names actually enforced
    /// by the runtime, with Vietnamese labels/descriptions. Static and identical
    /// for every caller. Safe default tools (máy tính, ngày giờ, phân tích
    /// JSON/CSV/XML) are always available and are not part of this whitelist.
    /// </summary>
    [HttpGet("tools")]
    public IActionResult ToolCatalog() => Ok(CustomAgentRules.ToolCatalog);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveCustomAgentRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new CreateCustomAgentCommand
        {
            TenantId = tenantId,
            UserId = userId,
            Name = request.Name,
            Description = request.Description,
            Icon = request.Icon,
            PersonaPrompt = request.PersonaPrompt,
            AllowedTools = request.AllowedTools,
            KnowledgeDocumentIds = request.KnowledgeDocumentIds,
            IsSharedWithTenant = request.IsSharedWithTenant
        }, ct);

        return result.Status switch
        {
            CustomAgentMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => CreatedAtAction(nameof(List), new { id = result.Agent!.Id }, result.Agent)
        };
    }

    /// <summary>Owner-only edit. 404 for missing or non-owner (never 403).</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCustomAgentRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new UpdateCustomAgentCommand
        {
            TenantId = tenantId,
            UserId = userId,
            AgentId = id,
            Name = request.Name,
            Description = request.Description,
            Icon = request.Icon,
            PersonaPrompt = request.PersonaPrompt,
            AllowedTools = request.AllowedTools,
            KnowledgeDocumentIds = request.KnowledgeDocumentIds,
            IsSharedWithTenant = request.IsSharedWithTenant
        }, ct);

        return result.Status switch
        {
            CustomAgentMutationStatus.NotFound => NotFound(),
            CustomAgentMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => NoContent()
        };
    }

    /// <summary>Owner-only delete. 404 for missing or non-owner (never 403).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var deleted = await _mediator.Send(new DeleteCustomAgentCommand
        {
            TenantId = tenantId,
            UserId = userId,
            AgentId = id
        }, ct);

        if (!deleted) return NotFound();
        return NoContent();
    }
}
