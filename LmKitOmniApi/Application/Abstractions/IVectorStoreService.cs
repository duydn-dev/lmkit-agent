namespace LmKitOmniApi.Application.Abstractions;

public class VectorSearchResult
{
    public Guid Id { get; set; }
    public float Score { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

/// <summary>
/// Canonical payload keys for metadata-aware retrieval. Centralized so the writers
/// (vectorization worker, ad-hoc ingest), the index registration and the filter
/// builder cannot drift apart on a field name.
/// </summary>
public static class VectorPayloadFields
{
    /// <summary>Coarse document type derived from the file extension (pdf/word/excel/…). Keyword-indexed.</summary>
    public const string DocType = "DocType";
    /// <summary>User-assigned single category. Keyword-indexed.</summary>
    public const string Category = "Category";
    /// <summary>User-assigned tags, stored as a keyword ARRAY (any-element match). Keyword-indexed.</summary>
    public const string Tags = "Tags";
    /// <summary>Provenance of the chunk: "upload" (worker), "knowledgebase"/"ocr" (ad-hoc). Keyword-indexed.</summary>
    public const string Source = "Source";
    /// <summary>Upload time as Unix SECONDS (fits a double exactly, unlike ticks). Integer-indexed for range filters.</summary>
    public const string UploadedAtUnix = "UploadedAtUnix";
}

/// <summary>
/// Optional metadata constraints ANDed onto a retrieval call, on TOP of the
/// tenant/scope/document filters each method already applies. Each non-empty list
/// is an OR-group ("field IN values"); the groups AND together. The upload-time
/// bounds filter the integer <see cref="VectorPayloadFields.UploadedAtUnix"/>
/// payload (Unix seconds). An all-empty filter (<see cref="IsEmpty"/>) is treated
/// as no constraint, so passing one is always safe.
/// </summary>
public sealed record RetrievalMetadataFilter
{
    public IReadOnlyList<string>? DocTypes { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Sources { get; init; }
    public long? UploadedAfterUnix { get; init; }
    public long? UploadedBeforeUnix { get; init; }

    public bool IsEmpty =>
        (DocTypes is null || DocTypes.Count == 0)
        && (Categories is null || Categories.Count == 0)
        && (Tags is null || Tags.Count == 0)
        && (Sources is null || Sources.Count == 0)
        && UploadedAfterUnix is null
        && UploadedBeforeUnix is null;
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
        RetrievalMetadataFilter? metadata = null,
        CancellationToken ct = default);
    Task EnsureCollectionExistsAsync(string collectionName, ulong vectorSize, CancellationToken ct = default);
    Task DeleteVectorsAsync(string collectionName, IReadOnlyList<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Deletes every point whose <paramref name="payloadField"/> equals
    /// <paramref name="value"/> (e.g. all schema points for one database
    /// connection) — the delete-before-reingest primitive for per-connection
    /// schema collections.
    /// </summary>
    Task DeleteByPayloadFilterAsync(string collectionName, string payloadField, string value, CancellationToken ct = default);

    /// <summary>
    /// H3 Fix: Search by payload keyword filter — independent of vector similarity.
    /// Uses Qdrant's native payload filtering for true sparse retrieval.
    /// </summary>
    Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(
        string collectionName, string payloadField, List<string> keywords,
        string tenantFilterField, string tenantId, int topK,
        RetrievalMetadataFilter? metadata = null, CancellationToken ct = default);

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
        RetrievalMetadataFilter? metadata = null,
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
        RetrievalMetadataFilter? metadata = null,
        CancellationToken ct = default);
}
