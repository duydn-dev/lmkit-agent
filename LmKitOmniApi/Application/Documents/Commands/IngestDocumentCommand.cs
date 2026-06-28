using MediatR;

namespace LmKitOmniApi.Application.Documents.Commands;

public class IngestDocumentCommand : IRequest<string>
{
    public Guid TenantId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
