using MediatR;

namespace LmKitOmniApi.Application.Canvas.Queries;

/// <summary>
/// Fetches one owned canvas artifact by root id — the latest version by
/// default, or the exact version when <see cref="Version"/> is provided.
/// Returns null (→ 404) for missing roots, foreign tenant/user roots, and
/// unknown versions alike, so ids are not enumerable.
/// </summary>
public class GetCanvasArtifactQuery : IRequest<CanvasArtifactDetailDto?>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RootId { get; set; }
    public int? Version { get; set; }
}

/// <summary>
/// Full artifact payload for one version row. <c>CreatedAt</c> is that
/// row's <c>CreatedAtUtc</c> (each saved version is an immutable row).
/// </summary>
public class CanvasArtifactDetailDto
{
    public Guid Id { get; set; }
    public Guid RootId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Language { get; set; }
    public int Version { get; set; }
    public string Content { get; set; } = string.Empty;
    public Guid? ChatSessionId { get; set; }
    public DateTime CreatedAt { get; set; }
}
