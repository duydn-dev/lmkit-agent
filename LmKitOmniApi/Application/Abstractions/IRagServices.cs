namespace LmKitOmniApi.Application.Abstractions;

public interface ITextChunkingService
{
    List<string> ChunkText(string text, int maxChunkSize = 1200, int overlap = 200);
}

public interface IRagPipelineService
{
    Task<string> IngestDocumentAsync(
        Guid tenantId,
        Guid userId,
        string fileName,
        string content,
        CancellationToken ct = default);
    /// <param name="documentIds">
    /// Optional document allowlist (custom-agent knowledge pinning). When
    /// non-empty, retrieval is restricted to chunks belonging to these documents
    /// IN ADDITION to the caller's tenant/owner access-scope filter — an
    /// intersection that can only narrow results, never widen access. Null keeps
    /// today's behavior exactly.
    /// </param>
    Task<string> QueryKnowledgeBaseAsync(
        Guid tenantId,
        Guid userId,
        string query,
        int topK = 3,
        CancellationToken ct = default,
        bool chatInferenceLeaseAlreadyHeld = false,
        IReadOnlyCollection<Guid>? documentIds = null);
}
