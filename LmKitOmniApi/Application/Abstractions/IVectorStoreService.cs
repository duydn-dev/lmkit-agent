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
    /// Dense search for the custom-agent knowledge-pinning path, constrained by
    /// BOTH the owning TENANT AND a document-id allowlist at once:
    /// (<paramref name="tenantField"/> == <paramref name="tenantId"/>)
    /// AND (<paramref name="documentIdField"/> IN <paramref name="documentIds"/>).
    /// Pinned documents may belong to a DIFFERENT user in the same tenant (a
    /// shared agent), so scoping here is tenant-wide, NOT the caller's private
    /// access scope. Callers without a document restriction use
    /// <see cref="SearchSimilarWithAnyPayloadAsync"/> (private access scope) instead.
    /// </summary>
    Task<List<VectorSearchResult>> SearchSimilarWithinDocumentsAsync(
        string collectionName,
        float[] queryVector,
        string tenantField,
        string tenantId,
        string documentIdField,
        IReadOnlyList<string> documentIds,
        int topK,
        CancellationToken ct = default);

    /// <summary>
    /// Sparse (keyword) equivalent of <see cref="SearchSimilarWithinDocumentsAsync"/>
    /// for the pinned-knowledge path: payload-filter search constrained by
    /// (<paramref name="tenantField"/> == <paramref name="tenantId"/>) AND
    /// (<paramref name="documentIdField"/> IN <paramref name="documentIds"/>) AND
    /// (any of <paramref name="keywords"/> present). Tenant-scoped, so a shared
    /// agent's pinned docs owned by another tenant member are reachable; the
    /// non-pinned keyword path keeps using <see cref="SearchByPayloadFilterAsync"/>
    /// with the caller's private access scope.
    /// </summary>
    Task<List<VectorSearchResult>> SearchByPayloadWithinDocumentsAsync(
        string collectionName,
        string payloadField,
        List<string> keywords,
        string tenantField,
        string tenantId,
        string documentIdField,
        IReadOnlyList<string> documentIds,
        int topK,
        CancellationToken ct = default);
}
