using MediatR;

namespace LmKitOmniApi.Application.Canvas.Commands;

/// <summary>
/// Appends a new version row to an owned root: Version = current max + 1 with
/// the same RootId/Kind/Language/ChatSessionId. A null <see cref="Title"/>
/// carries the previous version's title forward. Returns null (→ 404) when the
/// root does not exist for this tenant/user, so foreign roots look missing.
/// </summary>
public class UpdateCanvasArtifactCommand : IRequest<CanvasArtifactUpdatedDto?>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RootId { get; set; }
    public string? Title { get; set; }
    public string Content { get; set; } = string.Empty;
}

/// <summary>PUT response body: the freshly appended version row.</summary>
public class CanvasArtifactUpdatedDto
{
    public Guid Id { get; set; }
    public int Version { get; set; }
}
