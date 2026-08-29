using MediatR;

namespace LmKitOmniApi.Application.KnowledgeBase.Commands;

/// <summary>
/// Ingests raw text content into the tenant knowledge base via the RAG
/// pipeline. Property names match the previous request contract
/// (<c>fileName</c>/<c>content</c>); TenantId and UserId are always overwritten
/// by the controller from the authenticated principal.
/// </summary>
public class IngestKnowledgeCommand : IRequest<string>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
