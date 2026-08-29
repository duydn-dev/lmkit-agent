namespace LmKitOmniApi.Application.McpServers.Commands;

/// <summary>
/// JSON-bound request body for POST/PUT /api/mcp-servers. Moved verbatim from
/// McpServersController.cs — property names/casing are the wire contract and must not change.
/// </summary>
public sealed class SaveMcpServerRequest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string>? Headers { get; set; }
    public bool ReplaceHeaders { get; set; }
    public bool IsActive { get; set; } = true;
    public bool TrustReadOnlyAnnotations { get; set; }
}

/// <summary>
/// Shared payload for the create/update commands so both handlers run the exact same
/// validation (<see cref="McpServerRules.ValidateAsync"/>). TenantId is always set by the
/// controller from claims — never from the request body.
/// </summary>
public abstract class SaveMcpServerCommandBase
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string>? Headers { get; set; }
    public bool ReplaceHeaders { get; set; }
    public bool IsActive { get; set; } = true;
    public bool TrustReadOnlyAnnotations { get; set; }
}

/// <summary>
/// Outcome the controller maps back onto the original HTTP contract:
/// ValidationFailed → 400 with the raw string body, NameConflict → 409 with the raw string
/// body, NotFound → empty 404, Success → 201 (create) / 204 (update).
/// </summary>
public enum McpServerMutationStatus
{
    Success,
    NotFound,
    ValidationFailed,
    NameConflict
}

public sealed class SaveMcpServerResult
{
    public McpServerMutationStatus Status { get; init; }

    /// <summary>Raw string body for 400/409 responses (these endpoints do not wrap errors in an object).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Populated on successful create; serialized as the 201 body.</summary>
    public CreatedMcpServerDto? Server { get; init; }

    public static SaveMcpServerResult ValidationFailed(string? message) =>
        new() { Status = McpServerMutationStatus.ValidationFailed, ErrorMessage = message };

    public static SaveMcpServerResult NameConflict(string message) =>
        new() { Status = McpServerMutationStatus.NameConflict, ErrorMessage = message };

    public static SaveMcpServerResult NotFound() =>
        new() { Status = McpServerMutationStatus.NotFound };
}

/// <summary>
/// Mirrors the anonymous 201 payload previously built inline
/// (Id, Name, Url, IsActive, TrustReadOnlyAnnotations — declaration order preserved).
/// </summary>
public sealed class CreatedMcpServerDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool TrustReadOnlyAnnotations { get; init; }
}
