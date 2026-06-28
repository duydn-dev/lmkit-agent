using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("graph_relationships")]
public sealed class GraphRelationship
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid SourceEntityId { get; set; }

    public Guid TargetEntityId { get; set; }

    [MaxLength(128)]
    public string RelationType { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public double Weight { get; set; } = 1.0;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public GraphEntity? SourceEntity { get; set; }
    public GraphEntity? TargetEntity { get; set; }
}
