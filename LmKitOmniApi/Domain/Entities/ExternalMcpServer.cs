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
