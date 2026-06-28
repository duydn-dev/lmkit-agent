using MediatR;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Documents.Commands;

namespace LmKitOmniApi.Application.Documents.Handlers;

public class QueryDocumentCommandHandler : IRequestHandler<QueryDocumentCommand, string>
{
    private readonly IRagPipelineService _ragService;

    public QueryDocumentCommandHandler(IRagPipelineService ragService)
    {
        _ragService = ragService;
    }

    public async Task<string> Handle(QueryDocumentCommand request, CancellationToken cancellationToken)
    {
        return await _ragService.QueryKnowledgeBaseAsync(request.TenantId, request.Query, request.TopK);
    }
}
