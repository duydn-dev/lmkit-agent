using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("agent_memories")]
public sealed class AgentMemory
{
    [Key]
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? UserId { get; set; }

    [MaxLength(50)]
    public string MemoryType { get; set; } = "Fact";

    [MaxLength(200)]
    public string MemoryKey { get; set; } = string.Empty;

    public string MemoryValue { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? SourceContext { get; set; }

    public float Confidence { get; set; } = 0.5f;

    public bool IsConfirmed { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
}
