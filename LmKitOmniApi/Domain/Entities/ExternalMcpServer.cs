using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("external_mcp_servers")]
public sealed class ExternalMcpServer
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    public string? HeadersJson { get; set; }

    /// <summary>
    /// How outbound requests to this server are authenticated:
    /// <c>"Static"</c> (default) uses only <see cref="HeadersJson"/>;
    /// <c>"ClientCredentials"</c> fetches an OAuth 2.0 bearer token (RFC 6749 §4.4)
    /// from <see cref="OAuthTokenUrl"/> and injects it as an Authorization header.
    /// </summary>
    [MaxLength(32)]
    public string AuthMode { get; set; } = "Static";

    [MaxLength(300)]
    public string? OAuthClientId { get; set; }

    /// <summary>DataProtection-encrypted client secret (never returned to the UI).</summary>
    public string? OAuthClientSecretProtected { get; set; }

    [MaxLength(500)]
    public string? OAuthTokenUrl { get; set; }

    /// <summary>Optional space-separated OAuth scopes sent with the token request.</summary>
    [MaxLength(1000)]
    public string? OAuthScopes { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Allows readOnlyHint=true to skip HITL only when a tenant administrator has
    /// explicitly established an out-of-band trust relationship with this server.
    /// </summary>
    public bool TrustReadOnlyAnnotations { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
