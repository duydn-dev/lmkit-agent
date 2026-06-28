using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("graph_entities")]
public sealed class GraphEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid? DocumentId { get; set; }

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Type { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public Document? Document { get; set; }

    public ICollection<GraphRelationship> SourceRelationships { get; set; } = new List<GraphRelationship>();
    public ICollection<GraphRelationship> TargetRelationships { get; set; } = new List<GraphRelationship>();
}
