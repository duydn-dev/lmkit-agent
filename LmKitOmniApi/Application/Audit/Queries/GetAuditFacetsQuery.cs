using MediatR;

namespace LmKitOmniApi.Application.Audit.Queries;

/// <summary>
/// Returns the distinct filter values present in a tenant's audit log so the
/// admin activity view can offer dropdowns instead of free-text guessing.
/// Tenant-scoped by the controller from the authenticated principal.
/// </summary>
public sealed class GetAuditFacetsQuery : IRequest<AuditFacetsDto>
{
    public Guid TenantId { get; set; }
}

/// <summary>Distinct actor types, actions and entity types for the filter UI.</summary>
public sealed class AuditFacetsDto
{
    public List<string> ActorTypes { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    public List<string> EntityTypes { get; set; } = new();
}
