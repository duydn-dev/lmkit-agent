using MediatR;

namespace LmKitOmniApi.Application.CustomAgents.Queries;

/// <summary>
/// Lists the custom agents visible to the caller: their own agents plus every
/// agent in the tenant marked <c>IsSharedWithTenant</c>.
/// </summary>
public class GetCustomAgentsQuery : IRequest<List<CustomAgentDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>
/// Wire DTO for GET/POST /api/agents/custom. CSV entity fields are parsed
/// server-side into arrays; <see cref="PersonaPrompt"/> is populated for the
/// owner only (null for non-owner shared agents).
/// </summary>
public class CustomAgentDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? PersonaPrompt { get; set; }

    /// <summary>Null = the caller role's default tool set; non-null = whitelist (intersection).</summary>
    public List<string>? AllowedTools { get; set; }

    public List<Guid> KnowledgeDocumentIds { get; set; } = new();
    public bool IsSharedWithTenant { get; set; }
    public bool IsOwner { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One selectable entry of GET /api/agents/custom/tools.</summary>
public sealed class CustomAgentToolDto
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}
