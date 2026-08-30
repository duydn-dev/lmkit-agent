namespace LmKitOmniApi.Application.Abstractions;

public class VectorSearchResult
{
    public Guid Id { get; set; }
    public float Score { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

public interface IVectorStoreService
{
    Task UpsertVectorAsync(string collectionName, Guid id, float[] vector, Dictionary<string, object>? payload = null, CancellationToken ct = default);
    Task<List<VectorSearchResult>> SearchSimilarWithAnyPayloadAsync(
        string collectionName,
        float[] queryVector,
        string payloadField,
        IReadOnlyList<string> allowedValues,
        int topK,
        CancellationToken ct = default);
    Task EnsureCollectionExistsAsync(string collectionName, ulong vectorSize, CancellationToken ct = default);
    Task DeleteVectorsAsync(string collectionName, IReadOnlyList<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// H3 Fix: Search by payload keyword filter — independent of vector similarity.
    /// Uses Qdrant's native payload filtering for true sparse retrieval.
    /// </summary>
    Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(
        string collectionName, string payloadField, List<string> keywords,
        string tenantFilterField, string tenantId, int topK, CancellationToken ct = default);

    /// <summary>
    /// Dense search constrained by BOTH an access-scope allowlist AND a
    /// document-id allowlist at once (custom-agent knowledge pinning):
    /// (<paramref name="scopeField"/> IN <paramref name="allowedScopeValues"/>)
    /// AND (<paramref name="documentIdField"/> IN <paramref name="documentIds"/>).
    /// Both filters are mandatory here — callers without a document restriction
    /// use <see cref="SearchSimilarWithAnyPayloadAsync"/> instead.
    /// </summary>
    Task<List<VectorSearchResult>> SearchSimilarWithinDocumentsAsync(
        string collectionName,
        float[] queryVector,
        string scopeField,
        IReadOnlyList<string> allowedScopeValues,
        string documentIdField,
        IReadOnlyList<string> documentIds,
        int topK,
        CancellationToken ct = default);
}
