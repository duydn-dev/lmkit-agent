using LmKitOmniApi.Application.Projects.Queries;
using MediatR;

namespace LmKitOmniApi.Application.Projects.Commands;

/// <summary>
/// JSON-bound request body for POST/PUT /api/projects. Property names and casing
/// are the wire contract: { name, description?, icon?, instructions? }.
/// Every property is nullable on purpose: with nullable reference types enabled,
/// <c>[ApiController]</c> would otherwise auto-reject a missing name with English
/// ProblemDetails before the handler can emit the codebase's Vietnamese
/// <c>{ message }</c> errors (same convention as the canvas request DTOs).
/// </summary>
public sealed class SaveProjectRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Instructions { get; set; }
}

/// <summary>
/// Shared payload for the create/update commands so both handlers run the exact
/// same validation (<see cref="ProjectRules.Validate"/>). TenantId and UserId are
/// always set by the controller from claims — never from the body.
/// </summary>
public abstract class SaveProjectCommandBase
{
    public Guid TenantId { get; set; }

    /// <summary>The caller — and therefore the owner of the project.</summary>
    public Guid UserId { get; set; }

    /// <summary>Nullable until validated: <see cref="ProjectRules.Validate"/> rejects null/empty.</summary>
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Instructions { get; set; }
}

public sealed class CreateProjectCommand : SaveProjectCommandBase, IRequest<SaveProjectResult>
{
}

public sealed class UpdateProjectCommand : SaveProjectCommandBase, IRequest<SaveProjectResult>
{
    public Guid ProjectId { get; set; }
}

/// <summary>
/// Outcome the controller maps back onto the HTTP contract:
/// ValidationFailed → 400 { message }, NotFound → empty 404 (owner-only surfaces
/// never answer 403, so project ids are not enumerable), Success → 201 with the
/// DTO (create) / 204 (update).
/// </summary>
public enum ProjectMutationStatus
{
    Success,
    NotFound,
    ValidationFailed
}

public sealed class SaveProjectResult
{
    public ProjectMutationStatus Status { get; init; }

    /// <summary>Exact Vietnamese validation message for 400 responses.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Populated on successful create; serialized as the 201 body.</summary>
    public ProjectDto? Project { get; init; }

    public static SaveProjectResult ValidationFailed(string message) =>
        new() { Status = ProjectMutationStatus.ValidationFailed, ErrorMessage = message };

    public static SaveProjectResult NotFound() =>
        new() { Status = ProjectMutationStatus.NotFound };

    public static SaveProjectResult Success(ProjectDto? project = null) =>
        new() { Status = ProjectMutationStatus.Success, Project = project };
}
