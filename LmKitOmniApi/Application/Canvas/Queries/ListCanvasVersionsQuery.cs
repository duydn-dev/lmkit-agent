using MediatR;

namespace LmKitOmniApi.Application.Canvas.Queries;

/// <summary>
/// Lists every saved version of one owned root, newest first. Returns null
/// (→ 404) when the root has no rows for this tenant/user — every root has at
/// least one version, so "empty" only ever means missing or foreign.
/// </summary>
public class ListCanvasVersionsQuery : IRequest<List<CanvasVersionDto>?>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RootId { get; set; }
}

/// <summary>One row of the version history: enough to render and fetch it.</summary>
public class CanvasVersionDto
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
}
