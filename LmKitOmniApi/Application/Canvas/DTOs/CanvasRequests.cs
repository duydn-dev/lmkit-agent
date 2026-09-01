namespace LmKitOmniApi.Application.Canvas.DTOs;

/// <summary>
/// Body of <c>POST /api/canvas</c>. Wire shape:
/// <c>{ chatSessionId?, title, kind, language?, content }</c>.
/// Every property is nullable on purpose: with nullable reference types
/// enabled, <c>[ApiController]</c> would otherwise auto-reject missing fields
/// with English ProblemDetails before CanvasController can emit the codebase's
/// Vietnamese <c>{ message }</c> errors (same convention as
/// <c>RenameChatSessionRequest</c>).
/// </summary>
public class CreateCanvasArtifactRequest
{
    public Guid? ChatSessionId { get; set; }
    public string? Title { get; set; }
    public string? Kind { get; set; }
    public string? Language { get; set; }
    public string? Content { get; set; }
}

/// <summary>
/// Body of <c>PUT /api/canvas/{rootId}</c>. Wire shape: <c>{ title?, content }</c>.
/// An omitted or blank title keeps the previous version's title.
/// </summary>
public class UpdateCanvasArtifactRequest
{
    public string? Title { get; set; }
    public string? Content { get; set; }
}
