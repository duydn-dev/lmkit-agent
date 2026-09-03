using System.Text.Json.Serialization;
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

    /// <summary>"Static", "ClientCredentials" or "AuthorizationCode".</summary>
    public string AuthMode { get; set; } = "Static";

    // Non-secret OAuth config, returned so the edit form can pre-fill it. The JSON names are
    // pinned to lowercase "oauth*" so they round-trip with the admin UI: the default Web
    // camelCase policy would otherwise emit "oAuthClientId" (capital A), which the client
    // reads as "oauthClientId" and silently drops on read-back.
    [JsonPropertyName("oauthClientId")]
    public string? OAuthClientId { get; set; }
    [JsonPropertyName("oauthTokenUrl")]
    public string? OAuthTokenUrl { get; set; }
    [JsonPropertyName("oauthAuthorizeUrl")]
    public string? OAuthAuthorizeUrl { get; set; }
    [JsonPropertyName("oauthScopes")]
    public string? OAuthScopes { get; set; }

    /// <summary>Whether an encrypted client secret is stored. The secret itself is never returned.</summary>
    public bool HasOAuthSecret { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
