using MediatR;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Documents.Commands;

namespace LmKitOmniApi.Application.Documents.Handlers;

public class IngestDocumentCommandHandler : IRequestHandler<IngestDocumentCommand, string>
{
    private readonly IRagPipelineService _ragService;

    public IngestDocumentCommandHandler(IRagPipelineService ragService)
    {
        _ragService = ragService;
    }

    public async Task<string> Handle(IngestDocumentCommand request, CancellationToken cancellationToken)
    {
        return await _ragService.IngestDocumentAsync(request.TenantId, request.FileName, request.Content);
    }
}
