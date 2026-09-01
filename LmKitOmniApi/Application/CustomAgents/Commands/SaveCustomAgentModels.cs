using LmKitOmniApi.Application.CustomAgents.Queries;
using MediatR;

namespace LmKitOmniApi.Application.CustomAgents.Commands;

/// <summary>
/// JSON-bound request body for POST/PUT /api/agents/custom. Property names and
/// casing are the wire contract:
/// { name, description?, icon?, personaPrompt, allowedTools?: string[]|null,
///   knowledgeDocumentIds?: Guid[], isSharedWithTenant?: bool }.
/// </summary>
public sealed class SaveCustomAgentRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string PersonaPrompt { get; set; } = string.Empty;

    /// <summary>Null = role-default tools; non-null = whitelist of catalog tool names.</summary>
    public List<string>? AllowedTools { get; set; }

    public List<Guid>? KnowledgeDocumentIds { get; set; }
    public bool IsSharedWithTenant { get; set; }
}

/// <summary>
/// Shared payload for the create/update commands so both handlers run the exact
/// same validation (<see cref="CustomAgentRules.ValidateAsync"/>). TenantId and
/// UserId are always set by the controller from claims — never from the body.
/// </summary>
public abstract class SaveCustomAgentCommandBase
{
    public Guid TenantId { get; set; }

    /// <summary>The caller — and therefore the (existing or future) owner of the agent.</summary>
    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string PersonaPrompt { get; set; } = string.Empty;
    public List<string>? AllowedTools { get; set; }
    public List<Guid>? KnowledgeDocumentIds { get; set; }
    public bool IsSharedWithTenant { get; set; }
}

public sealed class CreateCustomAgentCommand : SaveCustomAgentCommandBase, IRequest<SaveCustomAgentResult>
{
}

public sealed class UpdateCustomAgentCommand : SaveCustomAgentCommandBase, IRequest<SaveCustomAgentResult>
{
    public Guid AgentId { get; set; }
}

/// <summary>
/// Outcome the controller maps back onto the HTTP contract:
/// ValidationFailed → 400 { message }, NotFound → empty 404 (owner-only surfaces
/// never answer 403, so agent ids are not enumerable), Success → 201 with the
/// DTO (create) / 204 (update).
/// </summary>
public enum CustomAgentMutationStatus
{
    Success,
    NotFound,
    ValidationFailed
}

public sealed class SaveCustomAgentResult
{
    public CustomAgentMutationStatus Status { get; init; }

    /// <summary>Exact Vietnamese validation message for 400 responses.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Populated on successful create; serialized as the 201 body.</summary>
    public CustomAgentDto? Agent { get; init; }

    public static SaveCustomAgentResult ValidationFailed(string message) =>
        new() { Status = CustomAgentMutationStatus.ValidationFailed, ErrorMessage = message };

    public static SaveCustomAgentResult NotFound() =>
        new() { Status = CustomAgentMutationStatus.NotFound };

    public static SaveCustomAgentResult Success(CustomAgentDto? agent = null) =>
        new() { Status = CustomAgentMutationStatus.Success, Agent = agent };
}
