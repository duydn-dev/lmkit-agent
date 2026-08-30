using LmKitOmniApi.Application.Projects.Commands;
using LmKitOmniApi.Application.Projects.Queries;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Application.Projects;

/// <summary>
/// Shared validation, normalization, DTO mapping and prompt composition for
/// projects (ChatGPT-Projects style: chat sessions grouped under shared
/// instructions). Create and update run the exact same rules, mirroring
/// <see cref="CustomAgents.CustomAgentRules"/>.
/// </summary>
public static class ProjectRules
{
    public const int MaxProjectsPerUser = 20;
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 300;
    public const int MaxIconLength = 16;
    public const int MaxInstructionsLength = 4000;

    /// <summary>
    /// Validates a create/update payload. Returns the exact Vietnamese error
    /// message for a 400 response, or null when the payload is valid.
    /// </summary>
    public static string? Validate(SaveProjectCommandBase request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "Tên dự án là bắt buộc.";
        if (name.Length > MaxNameLength)
            return $"Tên dự án không được vượt quá {MaxNameLength} ký tự.";

        if (request.Description?.Trim() is { Length: > MaxDescriptionLength })
            return $"Mô tả không được vượt quá {MaxDescriptionLength} ký tự.";
        if (request.Icon?.Trim() is { Length: > MaxIconLength })
            return $"Icon không được vượt quá {MaxIconLength} ký tự.";
        if (request.Instructions?.Trim() is { Length: > MaxInstructionsLength })
            return $"Hướng dẫn không được vượt quá {MaxInstructionsLength} ký tự.";

        return null;
    }

    /// <summary>Trims optional fields; whitespace-only collapses to null.</summary>
    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Composes the effective <c>AgentRequestOptions.PersonaPrompt</c> for a chat
    /// request from the session's project instructions and its custom-agent
    /// persona. Whitespace-only inputs are treated as absent.
    /// <list type="bullet">
    ///   <item>Neither present → null (default assistant persona).</item>
    ///   <item>Only the agent persona → the persona untouched — byte-identical to
    ///   a session with no project, so existing agent behavior never changes.</item>
    ///   <item>Only project instructions → the delimited project block.</item>
    ///   <item>Both → the project block first, then the persona under its own
    ///   heading so the two stay clearly delimited.</item>
    /// </list>
    /// </summary>
    public static string? ComposePersonaPrompt(string? projectInstructions, string? agentPersona)
    {
        var instructions = NormalizeOptional(projectInstructions);
        var persona = string.IsNullOrWhiteSpace(agentPersona) ? null : agentPersona;

        if (instructions is null) return persona;

        var projectBlock = "## Hướng dẫn dự án\n" + instructions;
        return persona is null
            ? projectBlock
            : projectBlock + "\n\n## Persona của agent\n" + persona;
    }

    /// <summary>Maps an entity to the wire DTO with a precomputed session count.</summary>
    public static ProjectDto ToDto(Project project, int sessionCount) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Description = project.Description,
        Icon = project.Icon,
        Instructions = project.Instructions,
        SessionCount = sessionCount,
        CreatedAt = project.CreatedAtUtc,
        UpdatedAt = project.UpdatedAtUtc
    };
}
