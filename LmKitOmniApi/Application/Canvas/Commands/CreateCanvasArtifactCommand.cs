using MediatR;
using LmKitOmniApi.Application.Canvas.Queries;

namespace LmKitOmniApi.Application.Canvas.Commands;

/// <summary>
/// Creates version 1 of a new canvas artifact (RootId = Id, Version = 1).
/// Wire-level validation and normalization (title/kind/language/content caps)
/// live in CanvasController so 400 payloads follow the codebase's Vietnamese
/// <c>{ message }</c> convention; the one DB-dependent rule enforced here is
/// that a supplied <see cref="ChatSessionId"/> must be the caller's own
/// session — a miss returns null, which the controller maps to the contract's
/// 400 "Phiên chat không hợp lệ".
/// </summary>
public class CreateCanvasArtifactCommand : IRequest<CanvasArtifactDetailDto?>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid? ChatSessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = "markdown";
    public string? Language { get; set; }
    public string Content { get; set; } = string.Empty;
}
