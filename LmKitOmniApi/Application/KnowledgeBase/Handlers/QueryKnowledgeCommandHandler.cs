using MediatR;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.KnowledgeBase.Commands;

namespace LmKitOmniApi.Application.KnowledgeBase.Handlers;

public class QueryKnowledgeCommandHandler : IRequestHandler<QueryKnowledgeCommand, string>
{
    private readonly IRagPipelineService _ragService;

    public QueryKnowledgeCommandHandler(IRagPipelineService ragService)
    {
        _ragService = ragService;
    }

    public async Task<string> Handle(QueryKnowledgeCommand request, CancellationToken cancellationToken)
    {
        return await _ragService.QueryKnowledgeBaseAsync(
            request.TenantId,
            request.UserId,
            request.Query,
            request.TopK,
            cancellationToken);
    }
}
