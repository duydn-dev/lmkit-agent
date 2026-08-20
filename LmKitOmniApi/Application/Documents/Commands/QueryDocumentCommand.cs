using MediatR;

namespace LmKitOmniApi.Application.Documents.Commands;

public class QueryDocumentCommand : IRequest<string>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Query { get; set; } = string.Empty;
    public int TopK { get; set; } = 3;
}
