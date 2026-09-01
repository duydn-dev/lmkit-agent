using MediatR;

namespace LmKitOmniApi.Application.KnowledgeBase.Commands;

/// <summary>
/// Answers a question against the tenant knowledge base via the RAG pipeline.
/// Property names match the previous request contract (<c>query</c>/<c>topK</c>);
/// TenantId and UserId are always overwritten by the controller from the
/// authenticated principal.
/// </summary>
public class QueryKnowledgeCommand : IRequest<string>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Query { get; set; } = string.Empty;
    public int TopK { get; set; } = 3;
}
