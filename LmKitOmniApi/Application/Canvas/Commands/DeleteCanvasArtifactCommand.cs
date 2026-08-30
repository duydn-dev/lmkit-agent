using MediatR;

namespace LmKitOmniApi.Application.Canvas.Commands;

/// <summary>
/// Deletes every version row of one owned root. Returns false (→ 404) when the
/// root has no rows for this tenant/user, so foreign and missing roots are
/// indistinguishable.
/// </summary>
public class DeleteCanvasArtifactCommand : IRequest<bool>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RootId { get; set; }
}
