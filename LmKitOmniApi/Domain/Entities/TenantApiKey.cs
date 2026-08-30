using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("tenant_api_keys")]
public sealed class TenantApiKey
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Human-readable label chosen at creation ("CI pipeline", "n8n"...).</summary>
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex hash of the raw key. The raw key is shown once and never stored.</summary>
    [MaxLength(128)]
    public string ApiKey { get; set; } = string.Empty;

    public int MaxRequests { get; set; }

    public int UsedRequests { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAtUtc { get; set; }

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
}
