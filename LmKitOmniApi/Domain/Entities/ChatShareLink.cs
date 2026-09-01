using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A read-only public share link for a chat session. Only the SHA-256 hex digest of
/// the share token is ever persisted — the raw token exists solely in the creation
/// response, so a database leak cannot resurrect working share URLs. Revocation is a
/// timestamp instead of a delete so rotations leave an auditable trail.
/// </summary>
[Table("chat_share_links")]
public sealed class ChatShareLink
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatSessionId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>SHA-256 hex digest (64 chars) of the raw share token.</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null while the link is active; stamped on revocation or rotation.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    public ChatSession? ChatSession { get; set; }
}
