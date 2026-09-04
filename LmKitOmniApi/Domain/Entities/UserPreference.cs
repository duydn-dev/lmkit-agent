using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// User-level custom instructions (ChatGPT-style "Custom instructions"): a per-user
/// persona injected into the system prompt of every chat the user starts. Exactly
/// one row per (tenant, user) — enforced by the unique index in
/// <see cref="Infrastructure.Data.HermesDbContext"/> and upserted by the
/// custom-instructions endpoint. Both text fields are optional; an all-empty row is
/// byte-identical to having no preferences at all.
/// </summary>
[Table("user_preferences")]
public sealed class UserPreference
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Free-form "what should the assistant know about you" context.</summary>
    [MaxLength(2000)]
    public string? AboutUser { get; set; }

    /// <summary>Free-form "how should the assistant respond" style guidance.</summary>
    [MaxLength(2000)]
    public string? ResponseStyle { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
