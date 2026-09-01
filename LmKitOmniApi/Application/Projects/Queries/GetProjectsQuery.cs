using MediatR;

namespace LmKitOmniApi.Application.Projects.Queries;

/// <summary>Lists the caller's projects (tenant+user scoped), newest first.</summary>
public sealed class GetProjectsQuery : IRequest<List<ProjectDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>
/// Wire DTO for /api/projects:
/// { id, name, description, icon, instructions, sessionCount, createdAt, updatedAt }.
/// </summary>
public sealed class ProjectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Instructions { get; set; }

    /// <summary>Number of the caller's chat sessions currently inside the project.</summary>
    public int SessionCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
