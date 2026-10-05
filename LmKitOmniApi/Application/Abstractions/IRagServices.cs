namespace LmKitOmniApi.Application.Abstractions;

public interface ITextChunkingService
{
    List<string> ChunkText(string text, int maxChunkSize = 1200, int overlap = 200);
}

public interface IRagPipelineService
{
    /// <param name="source">Provenance tag written to each chunk's payload (e.g. "knowledgebase", "ocr").</param>
    /// <param name="category">Optional single category applied to every chunk of this ingest.</param>
    /// <param name="tags">Optional tags applied to every chunk of this ingest (stored as a keyword array).</param>
    Task<string> IngestDocumentAsync(
        Guid tenantId,
        Guid userId,
        string fileName,
        string content,
        string source = "knowledgebase",
        string? category = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken ct = default);
    /// <param name="documentIds">
    /// Optional document allowlist (custom-agent knowledge pinning). When
    /// non-empty, retrieval is restricted to chunks belonging to these documents
    /// IN ADDITION to the caller's tenant/owner access-scope filter — an
    /// intersection that can only narrow results, never widen access. Null keeps
    /// today's behavior exactly.
    /// </param>
    /// <param name="metadata">
    /// Optional metadata-aware constraints (doc type / category / tags / source /
    /// upload-time window) ANDed onto retrieval. Null/empty keeps today's behavior.
    /// </param>
    Task<string> QueryKnowledgeBaseAsync(
        Guid tenantId,
        Guid userId,
        string query,
        int topK = 3,
        CancellationToken ct = default,
        bool chatInferenceLeaseAlreadyHeld = false,
        IReadOnlyCollection<Guid>? documentIds = null,
        RetrievalMetadataFilter? metadata = null);
}
