using MediatR;

namespace LmKitOmniApi.Application.Canvas.Queries;

/// <summary>
/// Lists the latest version of each canvas artifact owned by the caller,
/// optionally narrowed to one chat session. Ordered by the latest version's
/// <c>CreatedAtUtc</c> (the artifact's "updatedAt") descending, capped at 100.
/// </summary>
public class ListCanvasArtifactsQuery : IRequest<List<CanvasArtifactListItemDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }
}

/// <summary>
/// Light list projection of one root's latest version row — deliberately
/// excludes <c>Content</c> so the list stays cheap for large documents.
/// <c>UpdatedAt</c> is that version row's <c>CreatedAtUtc</c>.
/// </summary>
public class CanvasArtifactListItemDto
{
    public Guid Id { get; set; }
    public Guid RootId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Language { get; set; }
    public int Version { get; set; }
    public Guid? ChatSessionId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
