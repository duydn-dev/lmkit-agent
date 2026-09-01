using LmKitOmniApi.Application.Projects.Commands;
using LmKitOmniApi.Application.Projects.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// CRUD surface for projects (ChatGPT-Projects style): a named workspace that
/// groups chat sessions under shared instructions, which are injected into the
/// system prompt of every session inside the project. Projects are strictly
/// per-user (tenant+user scoped); a foreign or missing project always answers
/// 404, never 403, so ids are not enumerable.
/// </summary>
[ApiController]
[Route("api/projects")]
[Authorize]
public sealed class ProjectsController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public ProjectsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>The caller's projects, newest first, each with its live session count.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var projects = await _mediator.Send(new GetProjectsQuery { TenantId = tenantId, UserId = userId }, ct);
        return Ok(projects);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveProjectRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new CreateProjectCommand
        {
            TenantId = tenantId,
            UserId = userId,
            Name = request.Name,
            Description = request.Description,
            Icon = request.Icon,
            Instructions = request.Instructions
        }, ct);

        return result.Status switch
        {
            ProjectMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => CreatedAtAction(nameof(List), new { id = result.Project!.Id }, result.Project)
        };
    }

    /// <summary>Owner-only edit (stamps UpdatedAt). 404 for missing or foreign (never 403).</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveProjectRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new UpdateProjectCommand
        {
            TenantId = tenantId,
            UserId = userId,
            ProjectId = id,
            Name = request.Name,
            Description = request.Description,
            Icon = request.Icon,
            Instructions = request.Instructions
        }, ct);

        return result.Status switch
        {
            ProjectMutationStatus.NotFound => NotFound(),
            ProjectMutationStatus.ValidationFailed => BadRequest(new { message = result.ErrorMessage }),
            _ => NoContent()
        };
    }

    /// <summary>
    /// Owner-only delete. 404 for missing or foreign (never 403). The project's
    /// chat sessions survive and simply leave the project (FK SetNull).
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var deleted = await _mediator.Send(new DeleteProjectCommand
        {
            TenantId = tenantId,
            UserId = userId,
            ProjectId = id
        }, ct);

        if (!deleted) return NotFound();
        return NoContent();
    }

    /// <summary>
    /// The project's chat sessions, newest first, in the exact shape of the main
    /// GET /api/chat/sessions list. 404 when the project is missing or foreign.
    /// </summary>
    [HttpGet("{id:guid}/sessions")]
    public async Task<IActionResult> Sessions(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var sessions = await _mediator.Send(new GetProjectSessionsQuery
        {
            TenantId = tenantId,
            UserId = userId,
            ProjectId = id
        }, ct);

        return sessions is null ? NotFound() : Ok(sessions);
    }
}
