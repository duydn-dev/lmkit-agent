using MediatR;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.KnowledgeBase.Commands;

namespace LmKitOmniApi.Application.KnowledgeBase.Handlers;

public class IngestKnowledgeCommandHandler : IRequestHandler<IngestKnowledgeCommand, string>
{
    private readonly IRagPipelineService _ragService;

    public IngestKnowledgeCommandHandler(IRagPipelineService ragService)
    {
        _ragService = ragService;
    }

    public async Task<string> Handle(IngestKnowledgeCommand request, CancellationToken cancellationToken)
    {
        return await _ragService.IngestDocumentAsync(
            request.TenantId,
            request.UserId,
            request.FileName,
            request.Content,
            cancellationToken);
    }
}
