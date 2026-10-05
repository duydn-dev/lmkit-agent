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
        var metadata = new RetrievalMetadataFilter
        {
            DocTypes = Clean(request.DocTypes),
            Categories = Clean(request.Categories),
            Tags = Clean(request.Tags),
            Sources = Clean(request.Sources),
            UploadedAfterUnix = ToUnix(request.UploadedAfter),
            UploadedBeforeUnix = ToUnix(request.UploadedBefore)
        };

        return await _ragService.QueryKnowledgeBaseAsync(
            request.TenantId,
            request.UserId,
            request.Query,
            request.TopK,
            cancellationToken,
            metadata: metadata);
    }

    private static List<string>? Clean(List<string>? values)
    {
        var cleaned = values?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();
        return cleaned is { Count: > 0 } ? cleaned : null;
    }

    private static long? ToUnix(DateTime? value) => value is { } dt
        ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds()
        : null;
}
