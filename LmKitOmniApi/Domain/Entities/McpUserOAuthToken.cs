using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A per-user OAuth 2.0 authorization-code token for an <see cref="ExternalMcpServer"/>.
/// Unlike client-credentials tokens (which are process-cached and shared by the whole
/// tenant), authorization-code tokens are minted for a specific end user and MUST be
/// scoped by tenant + user + server. Access and refresh tokens are encrypted at rest via
/// <c>McpHeaderProtector</c> and are never returned to any API surface.
/// </summary>
[Table("mcp_user_oauth_tokens")]
public sealed class McpUserOAuthToken
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The <see cref="ExternalMcpServer"/> this token authenticates against.</summary>
    public Guid ServerId { get; set; }

    /// <summary>DataProtection-encrypted access token (never returned to the UI).</summary>
    public string AccessTokenProtected { get; set; } = string.Empty;

    /// <summary>DataProtection-encrypted refresh token, when the provider issued one.</summary>
    public string? RefreshTokenProtected { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>The scope actually granted by the provider (may differ from the request).</summary>
    [MaxLength(1000)]
    public string? Scope { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
