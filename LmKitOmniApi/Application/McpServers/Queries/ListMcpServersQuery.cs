using MediatR;

namespace LmKitOmniApi.Application.McpServers.Queries;

public class ListMcpServersQuery : IRequest<List<McpServerSummaryDto>>
{
    public Guid TenantId { get; set; }
}

/// <summary>
/// Mirrors the anonymous projection previously built inline in McpServersController.List.
/// Property names and declaration order are load-bearing for wire-identical JSON.
/// Headers are never returned — only whether any exist.
/// </summary>
public sealed class McpServerSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool TrustReadOnlyAnnotations { get; set; }
    public bool HasHeaders { get; set; }

    /// <summary>"Static" or "ClientCredentials".</summary>
    public string AuthMode { get; set; } = "Static";

    /// <summary>Non-secret OAuth config, returned so the edit form can pre-fill it.</summary>
    public string? OAuthClientId { get; set; }
    public string? OAuthTokenUrl { get; set; }
    public string? OAuthScopes { get; set; }

    /// <summary>Whether an encrypted client secret is stored. The secret itself is never returned.</summary>
    public bool HasOAuthSecret { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
