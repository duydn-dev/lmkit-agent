using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A workspace grouping chat sessions under shared context: per-project
/// instructions are injected into the system prompt of every session that
/// belongs to the project (ChatGPT-Projects style). Owned by a user in a tenant.
/// </summary>
[Table("projects")]
public sealed class Project
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    [MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Emoji or icon key shown in pickers.</summary>
    [MaxLength(16)]
    public string? Icon { get; set; }

    /// <summary>Instructions applied to every chat session inside the project.</summary>
    [MaxLength(4000)]
    public string? Instructions { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
}
