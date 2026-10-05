using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.KnowledgeBase.Commands;
using LmKitOmniApi.Application.KnowledgeBase.Handlers;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The explicit API filter path: QueryKnowledgeCommandHandler maps the request's
/// filter fields into a <see cref="RetrievalMetadataFilter"/> — trimming blanks,
/// converting dates to Unix seconds — and hands it to the RAG pipeline.
/// </summary>
public sealed class QueryKnowledgeFilterMappingTests
{
    private sealed class CapturingRag : IRagPipelineService
    {
        public RetrievalMetadataFilter? Captured;

        public Task<string> IngestDocumentAsync(
            Guid tenantId, Guid userId, string fileName, string content,
            string source = "knowledgebase", string? category = null,
            IReadOnlyList<string>? tags = null, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<string> QueryKnowledgeBaseAsync(
            Guid tenantId, Guid userId, string query, int topK = 3,
            CancellationToken ct = default, bool chatInferenceLeaseAlreadyHeld = false,
            IReadOnlyCollection<Guid>? documentIds = null, RetrievalMetadataFilter? metadata = null)
        {
            Captured = metadata;
            return Task.FromResult("context");
        }
    }

    [Fact]
    public async Task Handle_BuildsFilter_TrimmingBlanks_AndConvertingDates()
    {
        var rag = new CapturingRag();
        var handler = new QueryKnowledgeCommandHandler(rag);

        await handler.Handle(new QueryKnowledgeCommand
        {
            Query = "nước thải",
            DocTypes = new() { "pdf", "  ", "excel" },
            Tags = new() { " môi-trường ", "" },
            UploadedAfter = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }, CancellationToken.None);

        var f = rag.Captured;
        Assert.NotNull(f);
        Assert.Equal(new[] { "pdf", "excel" }, f!.DocTypes);   // blank dropped, trimmed
        Assert.Equal(new[] { "môi-trường" }, f.Tags);           // trimmed, blank dropped
        Assert.Null(f.Categories);                              // none supplied → null, not empty list
        Assert.Equal(
            new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            f.UploadedAfterUnix);
        Assert.Null(f.UploadedBeforeUnix);
        Assert.False(f.IsEmpty);
    }

    [Fact]
    public async Task Handle_WithNoFilters_PassesAnEmptyFilter()
    {
        var rag = new CapturingRag();
        var handler = new QueryKnowledgeCommandHandler(rag);

        await handler.Handle(new QueryKnowledgeCommand { Query = "xin chào" }, CancellationToken.None);

        Assert.NotNull(rag.Captured);
        Assert.True(rag.Captured!.IsEmpty);
    }
}
